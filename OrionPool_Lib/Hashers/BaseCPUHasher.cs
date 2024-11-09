using DrillX.Solver;
using Equix;
using NLog;
using OrionClientLib.Hashers.Models;
using OrionClientLib.Pools;
using OrionClientLib.Pools.Models;
using OrionClientLib.Utilities;
using Spectre.Console;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OrionClientLib.Hashers
{
    public abstract class BaseCPUHasher : IHasher
    {
        protected static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public IHasher.Hardware HardwareType => IHasher.Hardware.CPU;
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

        private nint _currentAffinity;

        public bool Initialize(IPool pool, int threads)
        {
            if (Initialized)
            {
                return false;
            }

            _pool = pool;
            _running = true;
            _threads = threads;
            _info = new HasherInfo();

            if (_pool != null)
            {
                _pool.OnChallengeUpdate += _pool_OnChallengeUpdate;
            }

            _taskRunner = new Task(Run, TaskCreationOptions.LongRunning);
            _taskRunner.Start();

            //Allocate memory required
            while (_solverQueue.Count < Environment.ProcessorCount)
            {
                _solverQueue.Enqueue(new Solver());
            }

            //Set process affinity
            if (OperatingSystem.IsWindows())
            {
                Process currentProcess = Process.GetCurrentProcess();

                //currentProcess.PriorityClass = ProcessPriorityClass.AboveNormal;
                _currentAffinity = currentProcess.ProcessorAffinity;

                List<CoreInfo> coreInformation = SystemInformation.GetCoreInformation();
                int totalThreads = coreInformation.Sum(x => x.ThreadCount);

                if(threads != totalThreads)
                {
                    nint processorMask = 0;

                    int totalLogical = Math.Clamp(threads - coreInformation.Count, 0, coreInformation.Count);

                    //Extra thread for the UI
                    //TODO: Modify to use dedicated threads with a specific affinity
                    if (threads < coreInformation.Count)
                    {
                        ++threads;
                    }
                    //1431655765
                    int loopCount = Math.Min(coreInformation.Count, threads);



                    for (int i =0; i < loopCount; i++)
                    {
                        CoreInfo cInfo = coreInformation[i];

                        AddThreadAffinity(cInfo.PhysicalMask);

                        if(totalLogical > 0 && cInfo.HasLogical)
                        {
                            AddThreadAffinity(cInfo.LogicalMask);

                            --totalLogical;
                        }

                        void AddThreadAffinity(ulong mask)
                        {
                            if(threads <= 0)
                            {
                                return;
                            }

                            processorMask |= (nint)mask;
                            --threads;
                        }
                    }

                    currentProcess.ProcessorAffinity = processorMask;
                }
            }

            return true;
        }

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
            _logger.Log(LogLevel.Debug, $"New challenge. Challenge Id: {challengeId}. Range: {startNonce} - {endNonce}");

            return true;
        }

        public async Task StopAsync()
        {
            _running = false;
            _newChallengeWait.Reset();

            if (_pool != null)
            {
                _pool.OnChallengeUpdate -= _pool_OnChallengeUpdate;
            }

            await _taskRunner.WaitAsync(CancellationToken.None);

            Exception lastError = null;

            //Dispose memory
            while (_solverQueue.TryDequeue(out Solver solver))
            {
                try
                {
                    solver.Dispose();
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            //Reset affinity
            if (OperatingSystem.IsWindows())
            {
                Process currentProcess = Process.GetCurrentProcess();

                currentProcess.ProcessorAffinity = _currentAffinity;
                //currentProcess.PriorityClass = ProcessPriorityClass.Normal;
            }

            //Attempts to dispose everything before throwing an error
            if (lastError != null)
            {
                _logger.Log(LogLevel.Error, lastError, $"Failed to clean up memory. Hasher: {Name}");
            }
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

        public abstract bool IsSupported();

        public void PauseMining()
        {
            _pauseMining.Reset();
        }

        public void ResumeMining()
        {
            _pauseMining.Set();
        }
    }
}
