using NLog;
using OrionClientLib.Hashers;
using OrionClientLib.Modules.Models;
using OrionClientLib.Pools;
using OrionClientLib.Pools.Models;
using Solnet.Wallet;
using Spectre.Console;
using Spectre.Console.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules
{
    public class MinerModule : IModule
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public string Name { get; } = "Start Mining";

        private Data _currentData;
        private CancellationTokenSource _cts;
        private bool _stop = false;
        private Layout _uiLayout = null;
        private Table _hashrateTable = null;
        private Table _poolInfoTable = null;
        private bool _paused = false;

        public async Task<ExecuteResult> ExecuteAsync(Data data)
        {
            _currentData = data;

            return new ExecuteResult { Exited = _stop, Renderer = _uiLayout };
        }

        public async Task ExitAsync()
        {
            _logger.Log(LogLevel.Debug, $"Exiting out of miner module...");

            IPool pool = _currentData.GetChosenPool();
            (IHasher cpuHasher, IHasher gpuHasher) = _currentData.GetChosenHasher();

            List<Task> tasks = new List<Task>();

            if(cpuHasher != null)
            {
                tasks.Add(cpuHasher.StopAsync());
            }

            if (gpuHasher != null)
            {
                tasks.Add(gpuHasher.StopAsync());
            }

            if (pool != null)
            {
                await pool.DisconnectAsync();

                pool.OnMinerUpdate -= Pool_OnMinerUpdate;
                pool.OnChallengeUpdate -= Pool_OnChallengeUpdate;
                pool.ResumeMining -= Pool_ResumeMining;
                pool.PauseMining -= Pool_PauseMining;

                if (cpuHasher != null)
                {
                    cpuHasher.OnHashrateUpdate -= Hasher_OnHashrateUpdate;
                }

                if (gpuHasher != null)
                {
                    gpuHasher.OnHashrateUpdate -= Hasher_OnHashrateUpdate;
                }
            }

            await Task.WhenAll(tasks);

            _cts.Cancel();
            _stop = true;
        }

        public async Task<bool> InitializeAsync(Data data)
        {
            _stop = false;
            _currentData = data;
            _cts = new CancellationTokenSource();

            IPool pool = _currentData.GetChosenPool();
            (IHasher cpuHasher, IHasher gpuHasher) = _currentData.GetChosenHasher();

            if(pool == null)
            {
                return false;
            }

            GenerateUI();

            _poolInfoTable = new Table();
            _poolInfoTable.AddColumns(pool.TableHeaders());

            _logger.Log(LogLevel.Debug, $"Checking setup requirements for '{pool.PoolName}'");

            (Wallet wallet, string publicKey) = await data.Settings.GetWalletAsync();

            pool.SetWalletInfo(wallet, publicKey);

            if (!await pool.SetupAsync(_cts.Token))
            {
                return false;
            }

            AnsiConsole.Clear();

            pool.OnMinerUpdate += Pool_OnMinerUpdate;
            pool.OnChallengeUpdate += Pool_OnChallengeUpdate;
            pool.ResumeMining += Pool_ResumeMining;
            pool.PauseMining += Pool_PauseMining;

            if(cpuHasher != null)
            {
                cpuHasher.OnHashrateUpdate += Hasher_OnHashrateUpdate;
            }

            if (gpuHasher != null)
            {
                gpuHasher.OnHashrateUpdate += Hasher_OnHashrateUpdate;
            }

            if(cpuHasher != null && cpuHasher is not DisabledHasher)
            {
                _logger.Log(LogLevel.Debug, $"Initializing {cpuHasher.Name} {cpuHasher.HardwareType} hasher");

                cpuHasher.Initialize(pool, data.Settings.CPUThreads);
            }

            if (gpuHasher != null && gpuHasher is not DisabledHasher)
            {
                _logger.Log(LogLevel.Debug, $"Initializing {gpuHasher.Name} {cpuHasher.HardwareType} hasher");

                gpuHasher.Initialize(pool, data.Settings.CPUThreads);
            }

            _logger.Log(LogLevel.Debug, $"Connecting to pool '{pool.PoolName}'");

            await pool.ConnectAsync(_cts.Token);

            return true;
        }

        private void Pool_OnChallengeUpdate(object? sender, NewChallengeInfo e)
        {
            _paused = false;
        }

        private void Pool_PauseMining(object? sender, EventArgs e)
        {
            (IHasher cpu, IHasher gpu) = _currentData.GetChosenHasher();

            cpu?.PauseMining();
            gpu?.PauseMining();

            _paused = true;

            for (int i = 0; i < _hashrateTable.Rows.Count; i++)
            {
                _hashrateTable.UpdateCell(i, 2, "[yellow]Paused[/]");
            }
        }

        private void Pool_ResumeMining(object? sender, EventArgs e)
        {
            (IHasher cpu, IHasher gpu) = _currentData.GetChosenHasher();

            cpu?.ResumeMining();
            gpu?.ResumeMining();
        }

        private void Pool_OnMinerUpdate(object? sender, string[] e)
        {
            if (e.Length != _poolInfoTable.Columns.Count)
            {
                _logger.Log(LogLevel.Warn, $"Pool info table expects {_poolInfoTable.Columns.Count} columns. Received: {e.Length}");
                return;
            }

            //Allows 10 rows
            if (_poolInfoTable.Rows.Count >= 10)
            {
                _poolInfoTable.RemoveRow(_poolInfoTable.Rows.Count - 1);
            }

            //Gets removed for some reason
            _poolInfoTable.Title = new TableTitle("Pool Info");
            _poolInfoTable.InsertRow(0, e);

            _uiLayout["poolInfo"].Update(_poolInfoTable);
        }

        private void Hasher_OnHashrateUpdate(object? sender, Hashers.Models.HashrateInfo e)
        {
            int index = e.IsCPU ? 0 : e.Index + 1;

            _hashrateTable.UpdateCell(index, 1, e.CurrentThreads.ToString());

            if (!_paused)
            {
                _hashrateTable.UpdateCell(index, 2, "[green]Mining[/]");
            }

            _hashrateTable.UpdateCell(index, 3, e.ChallengeSolutionsPerSecond.ToString());
            _hashrateTable.UpdateCell(index, 4, e.SolutionsPerSecond.ToString());
            _hashrateTable.UpdateCell(index, 5, e.HighestDifficulty.ToString());
            _hashrateTable.UpdateCell(index, 6, e.ChallengeId.ToString());
            //_hashrateTable.UpdateCell(index, 7, $"{e.TotalTime.TotalSeconds:0.00}s");
        }

        private void GenerateUI()
        {
            IPool pool = _currentData.GetChosenPool();
            (IHasher cpuHasher, IHasher gpuHasher) = _currentData.GetChosenHasher();

            //Generate UI
            _uiLayout = new Layout("minerModule").SplitColumns(
                new Layout("hashrate"),
                new Layout("poolInfo")
                );
            _uiLayout["hashrate"].Ratio = 75;
            _uiLayout["poolInfo"].Ratio = 100;
            
            _hashrateTable = new Table();
            _hashrateTable.Title = new TableTitle($"Pool: {pool.DisplayName}");

            _hashrateTable.AddColumn(new TableColumn("Hasher").Centered());
            _hashrateTable.AddColumn(new TableColumn("Threads").Centered());
            _hashrateTable.AddColumn(new TableColumn("Status").Centered());
            _hashrateTable.AddColumn(new TableColumn("Avg Hashrate").Centered());
            _hashrateTable.AddColumn(new TableColumn("Cur Hashrate").Centered());
            _hashrateTable.AddColumn(new TableColumn("Diff").Centered());
            _hashrateTable.AddColumn(new TableColumn("Id").Centered());
            //_hashrateTable.AddColumn(new TableColumn("Challenge Time").Centered());

            _poolInfoTable = new Table();
            _poolInfoTable.Title = new TableTitle("Pool Info");
            _poolInfoTable.AddColumns(pool.TableHeaders());
            _poolInfoTable.ShowRowSeparators = true;

            for(int i = 0; i < _poolInfoTable.Columns.Count; i++)
            {
                _poolInfoTable.Columns[i].Centered();
            }

            _poolInfoTable.Expand();

            _uiLayout["hashrate"].Update(_hashrateTable);
            _uiLayout["poolInfo"].Update(_poolInfoTable);

            //Add CPU
            _hashrateTable.AddRow(cpuHasher?.Name, "-", "-", "-", "-", "-", "-");


            if (gpuHasher != null)
            {
                //Will need to add a row for each GPU
            }
        }

    }
}
