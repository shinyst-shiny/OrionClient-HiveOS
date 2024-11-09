using Equix;
using NLog;
using Org.BouncyCastle.Crypto.Signers;
using OrionClientLib;
using OrionClientLib.Hashers;
using OrionClientLib.Hashers.CPU;
using OrionClientLib.Hashers.Models;
using OrionClientLib.Modules;
using OrionClientLib.Modules.Models;
using OrionClientLib.Pools;
using Solnet.Wallet;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Management;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Windows.Input;

namespace OrionClient
{
    public class Program
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private static (int Width, int Height) windowSize = (Console.WindowWidth, Console.WindowHeight);
        private static ConcurrentQueue<LogInformation> _logQueue = new ConcurrentQueue<LogInformation>();
        private static List<IPool> _pools;
        private static List<IModule> _modules;
        private static List<IHasher> _hashers;
        private static IModule _currentModule = null;
        private static bool _attemptedExit = false;
        private static Settings _settings;

        private static Layout _uiLayout;
        private static Table _logTable;

        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Settings))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Program))]
        static async Task Main(string[] args)
        {
            Console.Clear();

            if (!IsSupported())
            {
                Console.WriteLine($"Only x64 Windows/Linux is currently supported");

                return;
            }

            _settings = await Settings.LoadAsync();
            await _settings.SaveAsync();

            Console.CancelKeyPress += Console_CancelKeyPress;

            #region Configure

            _pools = new List<IPool>
            {
                new Ec1ipsePool(),
                new ShinystPool()
            };

            _modules = new List<IModule>
            {
                new MinerModule(),
                new PoolModule(),
                new BenchmarkerModule(),
                new SetupModule(),
                new ExitModule(),
            };

            _hashers = new List<IHasher>();
            AddSupportedHasher(new ManagedCPUHasher());
            AddSupportedHasher(new HybridCPUHasher());
            AddSupportedHasher(new HybridCPUHasherAVX2());
            AddSupportedHasher(new HybridCPUHasherAVX512());
            AddSupportedHasher(new NativeCPUHasher());
            AddSupportedHasher(new NativeCPUHasherAVX2());
            AddSupportedHasher(new DisabledCPUHasher());
            AddSupportedHasher(new DisabledGPUHasher());

            void AddSupportedHasher(IHasher hasher)
            {
                if(hasher.IsSupported())
                {
                    _hashers.Add(hasher);
                }
            }

            #endregion

            #region UI Configure

            _uiLayout = new Layout("Root").SplitRows(
                new Layout("Main"),
                new Layout("Logs"));


            _logTable = new Table();
            _logTable.AddColumn("Time");
            _logTable.AddColumn("Type");
            _logTable.AddColumn("Message");
            _logTable.NoBorder();

            _uiLayout["Logs"].Update(_logTable);

            #endregion

            while (true)
            {
                Data data = new Data(_hashers, _pools, _settings);

                await DisplayMenu(data);

                AnsiConsole.Clear();
            }
        }

        private static async void Console_CancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            //Allow exit
            if(_currentModule == null || _attemptedExit)
            {
                Environment.Exit(-1073741510);
            }

            //Prevent program from closing
            e.Cancel = true;
            _attemptedExit = true;

            await _currentModule.ExitAsync();
        }

        private static async Task DisplayMenu(Data data)
        {
            if(_currentModule == null)
            {
                _attemptedExit = false;

                _currentModule = await DisplayModuleSelector(data);

                try
                {
                    if (!await _currentModule.InitializeAsync(data))
                    {
                        _currentModule = null;
                    }
                }
                catch (TaskCanceledException)
                {
                    _currentModule = null;
                }

                return;
            }

            LiveDisplay liveLayout = AnsiConsole.Live(_uiLayout);

            await liveLayout.StartAsync(async (ctx) =>
            {
                while (true)
                {
                    ExecuteResult executeResult = await _currentModule.ExecuteAsync(data);

                    if(executeResult.Exited)
                    {
                        _currentModule = null;

                        break;
                    }

                    if (executeResult.Renderer != null)
                    {
                        _uiLayout["Main"].Update(executeResult.Renderer);
                        ctx.UpdateTarget(_uiLayout);
                    }
                    else
                    {
                        ctx.UpdateTarget(_logTable);
                    }

                    //Update logs
                    while (_logQueue.TryDequeue(out var log))
                    {
                        if (_logTable.Rows.Count > 10)
                        {
                            _logTable.Rows.RemoveAt(10);
                        }

                        string color = "gray";

                        if (log.Level == "Info")
                        {
                            color = "green";
                        }
                        else if (log.Level == "Warn")
                        {
                            color = "yellow";
                        }
                        else if (log.Level == "Error")
                        {
                            color = "red";
                        }

                        _logTable.InsertRow(0, $"[{color}]{log.Time}[/]", $"[{color}]{log.Level}[/]", $"[{color}]{Markup.Escape(log.Message)}[/]");
                    }

                    await Task.Delay(500);

                    //Changing window size can mess up with the display. Easiest method is to clear the UI and redraw
                    if(WindowSizeChange())
                    {
                        break;
                    }
                }
            });
        }

        private static async Task<IModule> DisplayModuleSelector(Data data)
        {
            SelectionPrompt<IModule> prompt = new SelectionPrompt<IModule>();
            (Wallet wallet, string publicKey) = await _settings.GetWalletAsync();
            (IHasher cpuHasher, IHasher gpuHasher) = data.GetChosenHasher();
            IPool pool = data.GetChosenPool();

            prompt.Title($"Wallet: {publicKey ?? "N/A"}\nHasher: CPU - {cpuHasher?.Name ?? "N/A"} ({_settings.CPUThreads} threads), GPU - {gpuHasher?.Name ?? "N/A"}\nPool: {pool?.DisplayName ?? "N/A"}");

            prompt.UseConverter((module) =>
            {
                if(module is PoolModule && pool != null)
                {
                    return pool.DisplayName;
                }

                return module.Name;
            });

            foreach(IModule module in _modules)
            {
                if(module is MinerModule)
                {
                    if(pool == null)
                    {
                        continue;
                    }

                    if ((cpuHasher is DisabledHasher || cpuHasher == null) && (gpuHasher is DisabledHasher || gpuHasher == null))
                    {
                        continue;
                    }
                }

                if(module is PoolModule)
                {
                    if (pool == null)
                    {
                        continue;
                    }

                    pool.SetWalletInfo(wallet, publicKey);
                }

                prompt.AddChoice(module);
            }

            return await prompt.ShowAsync(AnsiConsole.Console, CancellationToken.None);
        }

        private static bool WindowSizeChange()
        {
            if(Console.WindowHeight != windowSize.Height || Console.WindowWidth != windowSize.Width)
            {
                windowSize = (Console.WindowWidth, Console.WindowHeight);
                return true;
            }

            return false;
        }

        public static void LogMethod(string time, string level, string message, string exception)
        {
            _logQueue.Enqueue(new LogInformation
            {
                Time = time,
                Level = level,
                Message = message,
                Exception = exception
            });
        }

        static bool IsSupported()
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            {
                return false;
            }

            if(RuntimeInformation.OSArchitecture != Architecture.X64)
            {
                return false;
            }

            return true;
        }

        private class LogInformation
        {
            public string Time { get; set; }
            public string Level { get; set; } 
            public string Message { get; set; }
            public string Exception { get; set; }
        }
    }
}
