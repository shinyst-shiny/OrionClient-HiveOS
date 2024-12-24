using DrillX.Solver;
using Equix;
using System.Collections.Concurrent;
using System.Diagnostics;
using ILGPU;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime;
using OrionClientLib.Hashers.GPU;
using OrionClientLib.Modules.Vanity;
using System.Security.Cryptography;
using NLog;
using OrionClientLib.Utilities;
using Chaos.NaCl;
using System.IO;

namespace OrionClientLib.Hashers
{
    public class VanityGPU
    {
        protected static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private const int _maxQueueSize = 2;
        private static VanityFinder _vanityFinder = new VanityFinder();

        public IHasher.Hardware HardwareType => IHasher.Hardware.GPU;
        public bool Initialized => _taskRunner?.IsCompleted == false;
        public event EventHandler<VanityHashingInfo> OnHashrateUpdate;
        public bool InitializedVanities { get; private set; }
        public int FoundWallets => _vanityFinder.TotalWallets;
        public int FoundUniqueWallets => _vanityFinder.TotalUniqueWallets;
        public int SearchVanities => _vanityFinder.SearchVanities;
        public bool InvalidLines => _vanityFinder.InvalidCharacters > 0 || _vanityFinder.InvalidFormat > 0;
        public ConcurrentDictionary<int, VanityTracker> VanitiesByLength => _vanityFinder.VanitiesByLength;

        public string InvalidMessage => $"{(_vanityFinder.InvalidFormat > 0 ? $"Invalid Format: {_vanityFinder.InvalidFormat} lines. " : String.Empty)}" +
            $"{(_vanityFinder.InvalidCharacters > 0 ? $"Invalid Characters: {_vanityFinder.InvalidCharacters} vanities. Valid Characters: {String.Join(String.Empty, Base58EncoderPerf.PszBase58)}" : "")}";

        protected Stopwatch _sw = Stopwatch.StartNew();

        protected bool _running = false;
        protected bool _executing = false;
        protected bool _hasNotice => !_running;

        private VanityHashingInfo _info = new VanityHashingInfo();

        private Context _context;
        private Task _taskRunner;
        private int _threads = Environment.ProcessorCount;

        private List<GPUDeviceHasher> _gpuDevices = new List<GPUDeviceHasher>();
        private List<CPUData> _gpuData = new List<CPUData>();

        private BlockingCollection<CPUData> _availableCPUData = new BlockingCollection<CPUData>(new ConcurrentQueue<CPUData>());
        private BlockingCollection<CPUData> _setupCPUData = new BlockingCollection<CPUData>(new ConcurrentQueue<CPUData>());

        public async Task<(bool success, string message)> InitializeAsync(Settings settings)
        {
            if (Initialized)
            {
                return (false, "Already initialized");
            }

            if (settings.VanitySetting.GPUDevices == null || settings.VanitySetting.GPUDevices.Count == 0)
            {
                return (false, "No GPU devices selected. Select 'Setup' to select devices");
            }

            _running = true;
            VanitiesByLength.Values.ToList().ForEach(x => x.Reset());
            _threads = Environment.ProcessorCount;
            if(settings.VanitySetting.VanityThreads > 0)
            {
                _threads = settings.VanitySetting.VanityThreads;
            }

            _info = new VanityHashingInfo();

            _context = Context.Create(builder => builder.AllAccelerators().Profiling().IOOperations()
            .Inlining(InliningMode.Aggressive)
            .Optimize(OptimizationLevel.O0));

            IntrinsicsLoader.Load(typeof(VanityKernel), _context);

            var devices = _context.Devices.Where(x => x.AcceleratorType != AcceleratorType.CPU).ToList();

            List<Device> devicesToUse = new List<Device>();
            HashSet<Device> supportedDevices = new HashSet<Device>(GetDevices(true));

            foreach(var d in settings.VanitySetting.GPUDevices)
            {
                if(d >= 0 && d < devices.Count)
                {
                    if (supportedDevices.Contains(devices[d]))
                    {
                        devicesToUse.Add(devices[d]);
                    }
                }
            }

            if (devicesToUse.Count == 0)
            {
                _logger.Log(LogLevel.Warn, $"No supported GPU devices selected");

                return (false, $"No supported GPU devices selected");
            }

            var maxBytes = (ulong)settings.VanitySetting.MaxRAMUsageMB * 1024 * 1024;

            var usageFromGPU = (ulong)(_maxQueueSize * 3) * (ulong)CPUData.KeySize * (ulong)devicesToUse.Count; //3 allocations of batch size per GPU. CPU will use same amount
            var maxBatchSize = maxBytes / usageFromGPU;

            maxBatchSize = maxBatchSize / (ulong)settings.VanitySetting.GPUBlockSize * (ulong)settings.VanitySetting.GPUBlockSize;

            for (int i = 0; i < devicesToUse.Count; i++)
            {
                var device = devicesToUse[i];

                GPUDeviceHasher dHasher = new GPUDeviceHasher(GetVanityKernel(), device.CreateAccelerator(_context),
                                                              device, i, _setupCPUData, _availableCPUData, settings.VanitySetting.GPUBlockSize, settings.VanitySetting.VanityThreads);
                try
                {
                    dHasher.Initialize((int)maxBatchSize);

                    dHasher.OnHashrateUpdate += DHasher_OnHashrateUpdate;
                    _gpuDevices.Add(dHasher);
                }
                catch(Exception ex)
                {
                    _logger.Log(LogLevel.Error, ex, $"Failed to initialize device {device.Name} [{i}]. Reason: {ex.Message}");
                    dHasher?.Dispose();
                }
            }

            for (int i = 0; i < devicesToUse.Count * _maxQueueSize * 2; i++)
            {
                _availableCPUData.Add(new CPUData((int)maxBatchSize));
            }

            //Start CPU program generation thread
            _taskRunner = new Task(RunGeneration, TaskCreationOptions.LongRunning);
            _taskRunner.Start();

            return (true, String.Empty);
        }

        public async Task InitializeVanities(Settings settings)
        {
            InitializedVanities = true;

            string vanityDirectory = Path.Combine(Utils.GetExecutableDirectory(), Settings.VanitySettings.Directory);
            string inputFile = Path.Combine(vanityDirectory, settings.VanitySetting.VanitySearchFile);
            string outputFile = Path.Combine(vanityDirectory, settings.VanitySetting.VanityOutputFile);
            string walletDirectory = Path.Combine(vanityDirectory, "wallets");

            await _vanityFinder.Load(inputFile, outputFile, walletDirectory, settings.VanitySetting.MinimumCharacterLength);
        }

        private void DHasher_OnHashrateUpdate(object? sender, VanityHashingInfo e)
        {
            OnHashrateUpdate?.Invoke(this, e);
        }

        private async void RunGeneration()
        {
            try
            {
                while (_running)
                {
                    _executing = false;

                    CPUData cpuData = null;

                    if (!_running)
                    {
                        continue;
                    }

                    _executing = true;

                    while (!_hasNotice && !_availableCPUData.TryTake(out cpuData, 50))
                    {

                    }

                    if (_hasNotice)
                    {
                        if (cpuData != null)
                        {
                            _availableCPUData.TryAdd(cpuData);
                        }

                        continue;
                    }

                    TimeSpan start = _sw.Elapsed;

                    RandomNumberGenerator.Fill(cpuData.PrivateKeys);

                    cpuData.PrivateKeyGenerationTime = _sw.Elapsed - start;

                    if (_hasNotice)
                    {
                        if (cpuData != null)
                        {
                            _availableCPUData.TryAdd(cpuData);
                        }

                        continue;
                    }

                    _setupCPUData.TryAdd(cpuData);
                }
            }
            catch(Exception ex)
            {
                _logger.Log(LogLevel.Error, ex, $"Unknown exception occurred during GPU program generation. Message: {ex.Message}");
            }
        }

        public async Task StopAsync()
        {
            if (!_running)
            {
                return;
            }

            _running = false;

            List<Task> waitTasks = new List<Task>();
            
            //Wait for all devices to stop
            _gpuDevices.ForEach(x => waitTasks.Add(x.WaitForStopAsync()));
            await Task.WhenAll(waitTasks);

            //Clean up CPU Data
            while(_availableCPUData.TryTake(out var item) || _setupCPUData.TryTake(out item))
            {
                item.Dispose();
            }

            //Clean up memory
            _gpuDevices.ForEach(x =>
            {
                x.OnHashrateUpdate -= DHasher_OnHashrateUpdate;
                x.Dispose();
            });

            _gpuDevices.Clear();

            //Clean up GPUData
            _gpuData.ForEach(x => x.Dispose());
            _gpuData.Clear();

            _context?.Dispose();
        }

        private Action<RunData> GetVanityKernel()
        {
            return VanityKernel.Kernel;
        }

        #region GPU Implemention

        public bool IsSupported()
        {
            List<Device> validDevices = GetDevices(true);

            return validDevices?.Count > 0;
        }

        public List<Device> GetDevices(bool onlyValid)
        {
            try
            {
                using Context context = Context.Create((builder) => builder.AllAccelerators());

                if (onlyValid)
                {
                    return GetValidDevices(context.Devices);
                }
                else
                {
                    return context.Devices.Where(x => x.AcceleratorType != AcceleratorType.CPU).ToList();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        protected virtual List<Device> GetValidDevices(IEnumerable<Device> devices)
        {
            if(devices == null)
            {
                return new List<Device>();
            }

            return devices.Where(x => x.AcceleratorType != AcceleratorType.CPU && x is CudaDevice).ToList();
        }

        #endregion

        #region Generic Implementation

        public void SetThreads(int totalThreads)
        {
            _threads = totalThreads;
        }

        #endregion

        #region GPU Hasher

        private class GPUDeviceHasher : IDisposable
        {
            private Stopwatch _sw = Stopwatch.StartNew();
            public event EventHandler<VanityHashingInfo> OnHashrateUpdate;

            private int _deviceId = 0;
            private Accelerator _accelerator = null;
            private Device _device = null;
            private KernelConfig _vanityConfig;


            private List<GPUDeviceData> _deviceData = new List<GPUDeviceData>();

            private BlockingCollection<GPUDeviceData> _copyToData = new BlockingCollection<GPUDeviceData>(new ConcurrentQueue<GPUDeviceData>());
            private BlockingCollection<GPUDeviceData> _executeData = new BlockingCollection<GPUDeviceData>(new ConcurrentQueue<GPUDeviceData>());
            private BlockingCollection<GPUDeviceData> _copyFromData = new BlockingCollection<GPUDeviceData>(new ConcurrentQueue<GPUDeviceData>());

            private BlockingCollection<CPUData> _readyCPUData;
            private BlockingCollection<CPUData> _availableCPUData;

            private Action<RunData> _vanityMethod;

            private Action<KernelConfig, RunData> _vanityKernel;

            private int _maxBatchSize = 0;
            private const int _minBatchSize = 0;
            private int _currentBatchSize = 0;
            private int _blockSize = 128;
            private int _threads = 1;

            private Task _runningTask = null;
            private CancellationTokenSource _runToken;
            private bool _running => _runToken?.IsCancellationRequested != true;

            private Task _gpuCopyToTask = null;
            private Task _gpuCopyFromTask = null;

            private bool _hasNotice => !_running;
            private ulong _totalHashes = 0;

            public GPUDeviceHasher(Action<RunData> vanityKernel,
                         Accelerator accelerator, Device device, int deviceId,
                         BlockingCollection<CPUData> readyCPUData, 
                         BlockingCollection<CPUData> availableCPUData,
                         int blockSize,
                         int threads
                         )
            {
                _vanityMethod = vanityKernel;
                _readyCPUData = readyCPUData;
                _availableCPUData = availableCPUData;
                _deviceId = deviceId;
                _device = device;
                _accelerator = accelerator;
                _threads = threads;

                _blockSize = blockSize;
            }

            public async void Initialize(int batchSize)
            {
                _maxBatchSize = batchSize;
                UpdateConfig(512);

                uint[] folding = Folding8.Init();

                MemoryBuffer1D<uint, Stride1D.Dense> foldingData = _accelerator.Allocate1D(folding);

                foldingData.CopyFromCPU(folding);

                for (int i = 0; i < _maxQueueSize; i++)
                {
                    MemoryBuffer1D<byte, Stride1D.Dense> publicKeyData = _accelerator.Allocate1D<byte>(CPUData.KeySize * _maxBatchSize);
                    MemoryBuffer1D<byte, Stride1D.Dense> privateKeyData = _accelerator.Allocate1D<byte>(CPUData.KeySize * _maxBatchSize);
                    MemoryBuffer1D<byte, Stride1D.Dense> vanityKeyData = _accelerator.Allocate1D<byte>(CPUData.KeySize * _maxBatchSize);

                    GPUDeviceData deviceData = new GPUDeviceData(publicKeyData, privateKeyData, vanityKeyData, foldingData);
                    _deviceData.Add(deviceData);
                    _copyToData.TryAdd(deviceData);
                }

                _gpuCopyToTask = new Task(GPUCopyTo, TaskCreationOptions.LongRunning);
                _gpuCopyToTask.Start();

                _gpuCopyFromTask = new Task(GPUCopyFrom, TaskCreationOptions.LongRunning);
                _gpuCopyFromTask.Start();

                _runToken = new CancellationTokenSource();
                _runningTask = new Task(Execute, _runToken.Token, TaskCreationOptions.LongRunning);
                _runningTask.Start();

                _vanityKernel = _accelerator.LoadStreamKernel(_vanityMethod);
                _sw.Restart();

                _logger.Log(LogLevel.Debug, $"Initialized GPU device: {_device.Name} [{_deviceId}]");
            }

            private void UpdateConfig(int batchSize)
            {
                batchSize = Math.Min(_maxBatchSize, batchSize);
                batchSize = Math.Max(_minBatchSize, batchSize);

                int iterationCount = batchSize;
                int groupSize = _blockSize;

                var g = Math.Log2(groupSize);

                //Invalid setting
                if ((int)g != g)
                {
                    groupSize = 256;
                }

                _currentBatchSize = batchSize;

                _vanityConfig = new KernelConfig(
                    new Index3D((iterationCount + groupSize - 1) / groupSize, 1, 1),
                    new Index3D(groupSize, 1, 1)
                    );
            }

            public async Task WaitForStopAsync()
            {
                List<Task> remainingTasks = new List<Task> { _runningTask, _gpuCopyFromTask, _gpuCopyToTask };

                _runToken?.Cancel();
                await Task.WhenAll(remainingTasks);
                _runToken?.Dispose();

                try
                {
                    foreach (var deviceData in _deviceData)
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

            private void GPUCopyTo()
            {
                using var stream = _accelerator.CreateStream();

                try
                {
                    while (_running)
                    {
                        GPUDeviceData deviceData = null;

                        while (!GetDeviceData(GPUDeviceData.Stage.CopyTo, 50, out deviceData) && !_hasNotice)
                        {
                        }

                        CPUData cpuData = null;

                        while (!_readyCPUData.TryTake(out cpuData, 50) && !_hasNotice)
                        {

                        }

                        if (_hasNotice)
                        {
                            //Return data
                            if (cpuData != null)
                            {
                                _readyCPUData.TryAdd(cpuData);
                            }

                            if(deviceData != null)
                            {
                                _copyToData.TryAdd(deviceData);
                            }

                            continue;
                        }

                        if(cpuData.InUse)
                        {
                            _logger.Log(LogLevel.Warn, $"CPUData currently in use");
                        }

                        //Set as the current CPU data
                        deviceData.CurrentCPUData = cpuData;

                        cpuData.InUse = true;

                        //Copies to device.
                        deviceData.PrivateKeyData.View.CopyFromCPU(stream, cpuData.PrivateKeys);

                        //Wait for copy to finish
                        stream.Synchronize();

                        _executeData.TryAdd(deviceData);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log(LogLevel.Error, $"Unknown exception occurred during CPU->GPU copying. Reason: {ex.Message}");
                }
            }

            private void Execute()
            {
                try
                {
                    while (_running)
                    {
                        GPUDeviceData deviceData = null;

                        while (!GetDeviceData(GPUDeviceData.Stage.Execute, 50, out deviceData) && !_hasNotice)
                        {

                        }

                        if (_hasNotice)
                        {
                            if(deviceData != null)
                            {
                                _copyToData.TryAdd(deviceData);
                            }

                            continue;
                        }

                        deviceData.CurrentBatchSize = _currentBatchSize;

                        using var marker1 = _accelerator.AddProfilingMarker();
                        _vanityKernel(_vanityConfig, deviceData.RunData);
                        using var marker2 = _accelerator.AddProfilingMarker();

                        //Wait for kernels to finish
                        _accelerator.Synchronize();

                        _copyFromData.TryAdd(deviceData);
                        deviceData.ExecutionTime = marker2.MeasureFrom(marker1);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log(LogLevel.Error, $"Unknown exception occurred during GPU execution. Reason: {ex.Message}");
                }
            }

            private void GPUCopyFrom()
            {
                using var stream = _accelerator.CreateStream();

                //Use a single set of CPU data here rather than reusing to prevent issues
                using var publicKeys =  _accelerator.AllocatePageLocked1D<byte>(_maxBatchSize * CPUData.KeySize, false);
                using var vanityKeys =  _accelerator.AllocatePageLocked1D<byte>(_maxBatchSize * CPUData.KeySize, false);

                try
                {
                    while (_running)
                    {
                        GPUDeviceData deviceData = null;

                        while (!GetDeviceData(GPUDeviceData.Stage.CopyFrom, 50, out deviceData) && !_hasNotice)
                        {

                        }

                        if (_hasNotice)
                        {
                            if(deviceData != null)
                            {
                                _copyToData.TryAdd(deviceData);
                            }

                            continue;
                        }

                        //Copies back to host
                        //deviceData.PublicKeyData.CopyToCPU(stream, publicKeys);
                        //deviceData.VanityKeyData.CopyToCPU(stream, vanityKeys);

                        deviceData.PublicKeyData.View.CopyToPageLockedAsync(stream, publicKeys);
                        deviceData.VanityKeyData.View.CopyToPageLockedAsync(stream, vanityKeys);

                        //Wait for copy to finish
                        stream.Synchronize();

                        TimeSpan start = _sw.Elapsed;
                        deviceData.CurrentCPUData.InUse = false;


                        #region Vanity Search

                        //for(int i = 0; i < deviceData.CurrentBatchSize; i++)
                        //{
                        //    byte[] publicKey = Ed25519.ExpandedPrivateKeyFromSeed(deviceData.CurrentCPUData.PrivateKeys[(i * 32)..(i * 32 + 32)]);
                        //    byte[] calculatedPublicKey = publicKeys[(i * 32)..(i * 32 + 32)];

                        //    if(!publicKey.SequenceEqual(calculatedPublicKey))
                        //    {

                        //    }
                        //}
                        _vanityFinder.Find(deviceData.CurrentCPUData.PrivateKeys, publicKeys.GetArray(), vanityKeys.GetArray(), deviceData.CurrentBatchSize, _threads);

                        #endregion

                        TimeSpan vanityTime = _sw.Elapsed - start;

                        _totalHashes += (ulong)deviceData.CurrentBatchSize;

                        OnHashrateUpdate?.Invoke(this, new VanityHashingInfo
                        {
                            Index = _deviceId,
                            ExecutionTime = deviceData.ExecutionTime,
                            PrivateKeyGenerationTime = deviceData.CurrentCPUData.PrivateKeyGenerationTime,
                            VanitySearchTime = vanityTime,
                            CurrentBatchSize = deviceData.CurrentBatchSize,
                            Runtime = _sw.Elapsed,
                            SessionHashes = _totalHashes
                        });

                        int foundLocation = 0;

                        if(vanityTime.TotalSeconds > 1.5)
                        {
                            UpdateConfig(_currentBatchSize / 2);
                        }
                        else if(vanityTime.TotalSeconds <= 0.75)
                        {
                            UpdateConfig(_currentBatchSize * 2);
                        }

                        _copyToData.TryAdd(deviceData);

                        //Return data
                        _availableCPUData.TryAdd(deviceData.CurrentCPUData);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log(LogLevel.Error, $"Unknown exception occurred during GPU->CPU copying. Reason: {ex.Message}");
                }
    }

            public void ResetData()
            {
                //Remove all from collections
                while(_copyToData.TryTake(out var _) || _executeData.TryTake(out var _) || _copyFromData.TryTake(out var _))
                {

                }

                foreach(var deviceData in _deviceData)
                {
                    deviceData.CurrentCPUData = null;

                    _copyToData.TryAdd(deviceData);
                }
            }

            private bool GetDeviceData(GPUDeviceData.Stage stage, int timeout, out GPUDeviceData data)
            {
                switch (stage)
                {
                    case GPUDeviceData.Stage.CopyTo:
                        return _copyToData.TryTake(out data, timeout);
                    case GPUDeviceData.Stage.Execute:
                        return _executeData.TryTake(out data, timeout);
                    case GPUDeviceData.Stage.CopyFrom:
                        return _copyFromData.TryTake(out data, timeout);
                }

                data = null;
                return false;
            }

            public void Dispose()
            {
                try
                {
                    _accelerator?.Dispose();
                }
                catch(Exception ex)
                {
                    _logger.Log(LogLevel.Error, ex, $"Failed to clean up memory for GPU device");
                }
            }

            private class GPUDeviceData : IDisposable
            {
                public enum Stage { CopyTo, Execute, CopyFrom };

                public TimeSpan ExecutionTime { get; set; }

                public MemoryBuffer1D<byte, Stride1D.Dense> PrivateKeyData { get; private set; }
                public MemoryBuffer1D<byte, Stride1D.Dense> PublicKeyData { get; private set; }
                public MemoryBuffer1D<byte, Stride1D.Dense> VanityKeyData { get; private set; }
                public MemoryBuffer1D<uint, Stride1D.Dense> FoldingData { get; private set; }

                public RunData RunData { get; private set; }

                public CPUData CurrentCPUData { get; set; }
                public int CurrentBatchSize { get; set; }

                public GPUDeviceData(MemoryBuffer1D<byte, Stride1D.Dense> privateKeyData, MemoryBuffer1D<byte, Stride1D.Dense> publicKeyData, MemoryBuffer1D<byte, Stride1D.Dense> vanityKeyData, MemoryBuffer1D<uint, Stride1D.Dense> foldingData)
                {
                    PrivateKeyData = privateKeyData;
                    PublicKeyData = publicKeyData;
                    VanityKeyData = vanityKeyData;
                    FoldingData = foldingData;

                    RunData = new RunData(privateKeyData.View, publicKeyData.View, vanityKeyData.View, foldingData.View);
                }

                public void Dispose()
                {
                    PrivateKeyData?.Dispose();
                    PublicKeyData?.Dispose();
                    VanityKeyData?.Dispose();
                    FoldingData?.Dispose();
                }
            }
        }

        #endregion

        #region CPU Data

        private unsafe class CPUData : IDisposable
        {
            public const int KeySize = 32;

            public byte[] PrivateKeys { get; set; }
            //public byte[] PublicKeys { get; set; }
            //public byte[] VanityKeys { get; set; } //Only contains first 32 characters of vanity

            public bool InUse { get; set; }

            public TimeSpan PrivateKeyGenerationTime { get; set; }

            public CPUData(int batchSize)
            {
                PrivateKeys = new byte[KeySize * batchSize];
                //PublicKeys = new byte[KeySize * batchSize];
                //VanityKeys = new byte[KeySize * batchSize];
            }

            public void Dispose()
            {
                //NativeMemory.Free(_instructionData);
                //NativeMemory.Free(_keys);
                //NativeMemory.Free(_solutions);
            }
        }

        #endregion
    }
}
