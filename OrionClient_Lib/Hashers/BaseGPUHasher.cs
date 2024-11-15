using DrillX.Solver;
using Equix;
using NLog;
using OrionClientLib.Hashers.Models;
using OrionClientLib.Pools.Models;
using OrionClientLib.Pools;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using ILGPU;
using ILGPU.IR;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime;
using DrillX.Compiler;
using DrillX;

namespace OrionClientLib.Hashers
{
    public abstract class BaseGPUHasher : IHasher, IGPUHasher
    {
        private const int _maxNonces = 4096;
        private const int _maxQueueSize = 2;

        protected static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public IHasher.Hardware HardwareType => IHasher.Hardware.GPU;
        public bool Initialized => _taskRunner?.IsCompleted == false;
        public TimeSpan CurrentChallengeTime => _sw.Elapsed - _challengeStartTime;

        public event EventHandler<HashrateInfo> OnHashrateUpdate;
        public abstract string Name { get; }
        public abstract string Description { get; }

        protected Stopwatch _sw = Stopwatch.StartNew();
        protected TimeSpan _challengeStartTime;

        protected bool _running = false;
        protected bool _executing = false;
        protected HasherInfo _info = new HasherInfo();

        protected IPool _pool;
        protected ManualResetEvent _newChallengeWait = new ManualResetEvent(false);
        protected ManualResetEvent _pauseMining = new ManualResetEvent(true);

        protected bool ResettingChallenge => !_newChallengeWait.WaitOne(1);

        protected ConcurrentQueue<Solver> _solverQueue = new ConcurrentQueue<Solver>();

        private int _threads = Environment.ProcessorCount;
        private Task _taskRunner;

        private List<GPUDeviceHasher> _gpuDevices = new List<GPUDeviceHasher>();
        private List<CPUData> _gpuData = new List<CPUData>();
        private Context _context;

        public async Task<bool> InitializeAsync(IPool pool, Settings settings)
        {
            if (Initialized)
            {
                return false;
            }

            if (settings.GPUDevices == null || settings.GPUDevices.Count == 0)
            {
                return false;
            }

            _pool = pool;
            _running = true;
            //Use total CPU threads for now
            _threads = settings.CPUThreads; //TODO: Change to use remaining threads
            _info = new HasherInfo();

            if (_pool != null)
            {
                _pool.OnChallengeUpdate += _pool_OnChallengeUpdate;
            }

            _context = Context.Create(builder => builder.AllAccelerators().Profiling().IOOperations());

            var devices = _context.Devices.Where(x => x.AcceleratorType != AcceleratorType.CPU).ToList();

            List<Device> devicesToUse = new List<Device>();

            foreach(var d in settings.GPUDevices)
            {
                if(d >= 0 && d < devices.Count)
                {
                    devicesToUse.Add(devices[d]);
                }
            }


            int maxNonces = (int)((ulong)devicesToUse.Min(x => x.MemorySize) / GPUDeviceHasher.MemoryPerNonce);

            //Reduce to a power of 2
            maxNonces = (int)Math.Pow(2, (int)Math.Log2(maxNonces));

            //Reduce to 4096 being the max
            maxNonces = Math.Min(_maxNonces, maxNonces);

            foreach(var device in devicesToUse)
            {
                GPUDeviceHasher dHasher = new GPUDeviceHasher(HashxKernel, EquihashKernel, device.CreateAccelerator(_context), device);
            }

            return true;
        }

        public async Task StopAsync()
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            _newChallengeWait.Reset();

            if (_pool != null)
            {
                _pool.OnChallengeUpdate -= _pool_OnChallengeUpdate;
            }

            List<Task> waitTasks = new List<Task>();
            
            //Wait for all devices to stop
            _gpuDevices.ForEach(x => waitTasks.Add(x.WaitForStopAsync()));
            await Task.WhenAll(waitTasks);

            //Clean up memory
            _gpuDevices.ForEach(x => x.Dispose());
            _gpuDevices.Clear();

            //Clean up GPUData
            _gpuData.ForEach(x => x.Dispose());
            _gpuData.Clear();

            _context?.Dispose();
        }

        public List<Device> GetDevices()
        {
            try
            {
                using Context context = Context.Create((builder) => builder.AllAccelerators());

                return context.Devices.Where(x => x.AcceleratorType != AcceleratorType.CPU).ToList();
            }
            catch(Exception ex)
            {
                return null;
            }
        }

        public abstract void HashxKernel(ArrayView<Instruction> program, ArrayView<SipState> key, ArrayView<ulong> results);
        public abstract void EquihashKernel(ArrayView<ulong> values, ArrayView<ushort> solutions, ArrayView<ushort> globalHeap, ArrayView<uint> solutionCount);
        public abstract bool IsSupported();




        #region Fix later
        private void _pool_OnChallengeUpdate(object? sender, NewChallengeInfo e)
        {
            //Don't want to block pool module thread waiting for challenge to change
            Task.Run(() => NewChallenge(e.ChallengeId, e.Challenge, e.StartNonce, e.EndNonce));
        }

        public bool NewChallenge(int challengeId, Span<byte> challenge, ulong startNonce, ulong endNonce)
        {
            _newChallengeWait.Reset();

            //Sequence is same as previous
            if (challenge.SequenceEqual(_info.Challenge))
            {
                return true;
            }

            //Stopping current execution should be relatively fast
            while (_executing)
            {
                Thread.Sleep(50);
            }

            _info.NewChallenge(startNonce, endNonce, challenge.ToArray(), challengeId);
            _newChallengeWait.Set();
            _pauseMining.Set();
            _challengeStartTime = _sw.Elapsed;
            _logger.Log(LogLevel.Debug, $"[CPU] New challenge. Challenge Id: {challengeId}. Range: {startNonce} - {endNonce}");

            return true;
        }

        protected virtual void Run()
        {
            while (_running)
            {
                _executing = false;

                while ((!_newChallengeWait.WaitOne(500) || !_pauseMining.WaitOne(0)) && _running)
                {
                }

                if (!_running)
                {
                    break;
                }

                _executing = true;

                TimeSpan startTime = _sw.Elapsed;
                int prevDifficulty = _info.DifficultyInfo.BestDifficulty;
                ulong startSolutions = _info.TotalSolutions;

                //TODO: Verify threads didn't increase, log error if it did
                _threads = Math.Min(_solverQueue.Count, _threads);

                var rangePartitioner = Partitioner.Create(0, (int)_info.BatchSize, (int)_info.BatchSize / _threads);
                ConcurrentQueue<Exception> exceptions = new ConcurrentQueue<Exception>();

                Parallel.ForEach(rangePartitioner, new ParallelOptions { MaxDegreeOfParallelism = _threads }, (range, loop) => ExecuteThread(range, loop, exceptions));

                if (exceptions.TryDequeue(out Exception ex))
                {
                    //Log error
                    Console.WriteLine(ex);
                }

                TimeSpan hashingTime = _sw.Elapsed - startTime;

                //All prior hashes are invalid now
                if (ResettingChallenge)
                {
                    continue;
                }

                OnHashrateUpdate?.Invoke(this, new HashrateInfo
                {
                    IsCPU = true,
                    ExecutionTime = hashingTime,
                    NumNonces = _info.BatchSize,
                    NumSolutions = _info.TotalSolutions - startSolutions,
                    HighestDifficulty = _info.DifficultyInfo.BestDifficulty,
                    ChallengeSolutions = _info.TotalSolutions,
                    TotalTime = _sw.Elapsed - _challengeStartTime,
                    CurrentThreads = _threads,
                    ChallengeId = _info.ChallengeId
                });

                //Modify batch size to be between 750ms-2000ms long
                if (_running)
                {
                    if (hashingTime.TotalSeconds < 0.75)
                    {
                        _info.BatchSize *= 2;
                    }
                    else if (hashingTime.TotalSeconds > 2)
                    {
                        _info.BatchSize /= 2;

                        _info.BatchSize = Math.Max(64, _info.BatchSize);
                    }
                }

                //Higher difficulty found, notify pool
                if (_info.DifficultyInfo.BestDifficulty > prevDifficulty)
                {
                    //Check that we aren't paused
                    if (_pauseMining.WaitOne(0))
                    {
                        _pool?.DifficultyFound(_info.DifficultyInfo.GetUpdateCopy());
                    }
                }

                _info.CurrentNonce += _info.BatchSize;
            }
        }

        protected abstract void ExecuteThread(Tuple<int, int> range, ParallelLoopState loopState, ConcurrentQueue<Exception> exceptions);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected int CalculateTarget(ReadOnlySpan<byte> s)
        {
            int totalBits = 0;

            for (int i = 0; i < s.Length; i++)
            {
                var t = BitOperations.LeadingZeroCount(s[i]) - 24;
                totalBits += t;

                if (t < 8)
                {
                    break;
                }
            }

            return totalBits;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void Reorder(Span<EquixSolution> solution)
        {
            //Individual level
            Span<ushort> v = MemoryMarshal.Cast<EquixSolution, ushort>(solution);

            for (int i = 0; i < v.Length; i += 2)
            {
                if (v[i] > v[i + 1])
                {
                    var t = v[i];
                    v[i] = v[i + 1];
                    v[i + 1] = t;
                }
            }

            //First pair
            Span<uint> v2 = MemoryMarshal.Cast<ushort, uint>(v);

            for (int i = 0; i < v2.Length; i += 2)
            {
                if (v2[i] > v2[i + 1])
                {
                    var t = v2[i];
                    v2[i] = v2[i + 1];
                    v2[i + 1] = t;
                }
            }


            //Second pair
            Span<ulong> v4 = MemoryMarshal.Cast<uint, ulong>(v2);

            for (int i = 0; i < v4.Length; i += 2)
            {
                if (v4[i] > v4[i + 1])
                {
                    var t = v4[i];
                    v4[i] = v4[i + 1];
                    v4[i + 1] = t;
                }
            }
        }

        public void SetThreads(int totalThreads)
        {
            _threads = totalThreads;
        }


        protected bool HasNativeFile()
        {
            string file = Path.Combine(AppContext.BaseDirectory, $"libequix.{(OperatingSystem.IsWindows() ? "dll" : "so")}");

            return File.Exists(file);
        }

        public void PauseMining()
        {
            _pauseMining.Reset();
        }

        public void ResumeMining()
        {
            _pauseMining.Set();
        }

        #endregion

        private class GPUDeviceHasher : IDisposable
        {
            public const ulong ProgramSize = (Instruction.ProgramSize * Instruction.Size);
            public const ulong KeySize = SipState.Size;
            public const ulong HeapSize = 2239488;
            public const ulong SolutionSize = EquixSolution.Size * EquixSolution.MaxLength;
            public const ulong HashSolutionSize = ushort.MaxValue + 1 * sizeof(ulong);
            public const ulong MemoryPerNonce = (ProgramSize + KeySize) * _maxQueueSize + HeapSize + SolutionSize + HashSolutionSize;

            private List<GPUDeviceData> _deviceData = new List<GPUDeviceData>();

            //Used to verify GPU solutions
            private Solver _solver = new Solver();

            private Accelerator _accelerator = null;
            private Device _device = null;

            private Action<ArrayView<Instruction>, ArrayView<SipState>, ArrayView<ulong>> _hashxKernel;
            private Action<ArrayView<ulong>, ArrayView<ushort>, ArrayView<ushort>, ArrayView<uint>> _equihashKernel;

            private int _nonceCount = 0;

            private Task _runningTask = null;
            private CancellationTokenSource _runToken;

            private Task _gpuCopyToTask = null;
            private CancellationTokenSource _gpuCopyToToken;
            private Task _gpuCopyFromTask = null;
            private CancellationTokenSource _gpuCopyFromToken;

            public GPUDeviceHasher(Action<ArrayView<Instruction>, ArrayView<SipState>, ArrayView<ulong>> hashxKernel,
                         Action<ArrayView<ulong>, ArrayView<ushort>, ArrayView<ushort>, ArrayView<uint>> equihashKernel,
                         Accelerator accelerator, Device device)
            {
                _hashxKernel = hashxKernel;
                _equihashKernel = equihashKernel;

                _device = device;
                _accelerator = accelerator;
            }

            public void Initialize(int totalNonces)
            {
                _nonceCount = totalNonces;

                MemoryBuffer1D<ushort, Stride1D.Dense> heap = _accelerator.Allocate1D<ushort>(_nonceCount);
                MemoryBuffer1D<ulong, Stride1D.Dense> hashes = _accelerator.Allocate1D<ulong>(_nonceCount);
                MemoryBuffer1D<EquixSolution, Stride1D.Dense> solutions = _accelerator.Allocate1D<EquixSolution>(_nonceCount * EquixSolution.MaxLength);


                for (int i = 0; i < _maxQueueSize; i++)
                {
                    MemoryBuffer1D<Instruction, Stride1D.Dense> instructions = _accelerator.Allocate1D<Instruction>(_nonceCount);
                    MemoryBuffer1D<SipState, Stride1D.Dense> keys = _accelerator.Allocate1D<SipState>(_nonceCount);

                    GPUDeviceData deviceData = new GPUDeviceData(instructions, keys, heap, hashes, solutions);
                    _deviceData.Add(deviceData);
                }

                _gpuCopyToTask = new Task(GPUCopyTo, _gpuCopyToToken.Token, TaskCreationOptions.LongRunning);
                _gpuCopyToTask.Start();

                _gpuCopyFromTask = new Task(GPUCopyFrom, _gpuCopyFromToken.Token, TaskCreationOptions.LongRunning);
                _gpuCopyFromTask.Start();

                _runningTask = new Task(Run, _runToken.Token, TaskCreationOptions.LongRunning);
                _runningTask.Start();

                _logger.Log(LogLevel.Debug, $"Initialized GPU device: {_device.Name}");
            }

            public async Task WaitForStopAsync()
            {
                _runToken.Cancel();
                await _runningTask.WaitAsync(CancellationToken.None);
                _runToken.Dispose();

                try
                {
                    foreach(var deviceData in _deviceData)
                    {
                        deviceData.Dispose();
                    }

                    _deviceData.Clear();
                }
                catch (Exception ex)
                {
                    _logger.Log(LogLevel.Error, ex, $"Failed to dispose GPU data");
                }
            }

            private async void GPUCopyTo()
            {
                while(!_gpuCopyToTask.IsCanceled)
                {
                    GPUDeviceData deviceData = GetDeviceData(GPUDeviceData.Stage.None);

                    while (deviceData == null && !_runningTask.IsCanceled)
                    {
                        await Task.Delay(100);
                        deviceData = GetDeviceData(GPUDeviceData.Stage.None);
                    }

                    if(_runningTask.IsCanceled)
                    {
                        break;
                    }

                    //TODO: Waits for hashx data

                    //Set as the current CPU data
                    //Copies to device
                    //Waits

                    deviceData.CurrentStage = GPUDeviceData.Stage.Execute;
                }
            }

            private async void GPUCopyFrom()
            {
                while (!_gpuCopyToTask.IsCanceled)
                {
                    GPUDeviceData deviceData = GetDeviceData(GPUDeviceData.Stage.Solution);

                    while (deviceData == null && !_runningTask.IsCanceled)
                    {
                        await Task.Delay(100);
                        deviceData = GetDeviceData(GPUDeviceData.Stage.Solution);
                    }

                    if (_runningTask.IsCanceled)
                    {
                        break;
                    }

                    //Copies back to host
                    //Waits
                    //Deals with verifying solution

                    deviceData.CurrentStage = GPUDeviceData.Stage.None;
                }
            }

            private async void Run()
            {
                while (!_runningTask.IsCanceled)
                {
                    GPUDeviceData deviceData = GetDeviceData(GPUDeviceData.Stage.Execute);

                    while (deviceData == null && !_runningTask.IsCanceled)
                    {
                        await Task.Delay(100);
                        deviceData = GetDeviceData(GPUDeviceData.Stage.Execute);
                    }

                    if (_runningTask.IsCanceled)
                    {
                        break;
                    }

                    //_hashxKernel();
                    //_equihashKernel();


                    deviceData.CurrentStage = GPUDeviceData.Stage.Solution;
                }
            }

            private GPUDeviceData GetDeviceData(GPUDeviceData.Stage stage)
            {
                return _deviceData.FirstOrDefault(x => x.CurrentStage == stage);
            }


            public void Dispose()
            {
                try
                {
                    _solver.Dispose();
                    _accelerator?.Dispose();
                }
                catch(Exception ex)
                {
                    _logger.Log(LogLevel.Error, ex, $"Failed to clean up memory for GPU device");
                }
            }

            private class GPUDeviceData : IDisposable
            {
                public enum Stage { None, Execute, Solution };

                public Stage CurrentStage { get; set; } = Stage.None;

                //Unique
                public MemoryBuffer1D<Instruction, Stride1D.Dense> ProgramInstructions { get; private set; }
                public MemoryBuffer1D<SipState, Stride1D.Dense> Keys { get; private set; }

                //These only need a single copy
                public MemoryBuffer1D<ushort, Stride1D.Dense> Heap { get; private set; }
                public MemoryBuffer1D<ulong, Stride1D.Dense> Hashes { get; private set; }
                public MemoryBuffer1D<EquixSolution, Stride1D.Dense> Solutions { get; private set; }

                public CPUData CurrentCPUData { get; set; }

                public GPUDeviceData(MemoryBuffer1D<Instruction, Stride1D.Dense> programInstructions, 
                                     MemoryBuffer1D<SipState, Stride1D.Dense> keys,
                                     MemoryBuffer1D<ushort, Stride1D.Dense> heap,
                                     MemoryBuffer1D<ulong, Stride1D.Dense> hashes,
                                     MemoryBuffer1D<EquixSolution, Stride1D.Dense> solutions)
                {
                    ProgramInstructions = programInstructions;
                    Keys = keys;
                    Heap = heap;
                    Hashes = hashes;
                    Solutions = solutions;
                }

                public void Dispose()
                {
                    ProgramInstructions?.Dispose();
                    Keys?.Dispose();
                }
            }
        }


        private unsafe class CPUData : IDisposable
        {
            private Instruction* _instructionData;
            private SipState* _keys;
            private EquixSolution* _solutions;

            public CPUData(int nonceCount)
            {
                _instructionData = (Instruction*)NativeMemory.Alloc((nuint)(sizeof(Instruction) * nonceCount * Instruction.ProgramSize));
                _keys = (SipState*)NativeMemory.Alloc((nuint)(sizeof(SipState) * nonceCount * Instruction.ProgramSize));
                _solutions = (EquixSolution*)NativeMemory.Alloc((nuint)(sizeof(Instruction) * nonceCount * Instruction.ProgramSize));
            }

            public void Dispose()
            {
                NativeMemory.Free(_instructionData);
                NativeMemory.Free(_keys);
                NativeMemory.Free(_solutions);
            }
        }
    }
}
