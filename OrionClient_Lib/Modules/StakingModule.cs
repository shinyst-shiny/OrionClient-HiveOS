using Blake2Sharp;
using Equix;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime.OpenCL;
using Newtonsoft.Json;
using NLog;
using OrionClientLib.CoinPrograms;
using OrionClientLib.CoinPrograms.Ore;
using OrionClientLib.Hashers;
using OrionClientLib.Modules.Models;
using OrionClientLib.Modules.SettingsData;
using OrionClientLib.Modules.Staking;
using OrionClientLib.Pools;
using OrionClientLib.Utilities;
using Solnet.Programs;
using Solnet.Programs.Utilities;
using Solnet.Rpc;
using Solnet.Rpc.Core.Http;
using Solnet.Rpc.Core.Sockets;
using Solnet.Rpc.Models;
using Solnet.Wallet;
using Solnet.Wallet.Bip39;
using Solnet.Wallet.Utilities;
using Spectre.Console;
using Spectre.Console.Rendering;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules
{
    public class StakingModule : IModule
    {
        private static readonly string KaminoPoolUrl = "https://api.kamino.finance/strategies/metrics?env=mainnet-beta&status=LIVE";
        private static readonly string MeteroaPoolUrl = "https://app.meteora.ag/amm/pools?address={0}";
        private static readonly PublicKey _solMint = new PublicKey("So11111111111111111111111111111111111111111");

        private static readonly Logger _logger = LogManager.GetLogger("Main");
        private const int PoolUpdateDelayMin = 60;

        public string Name { get; } = "Ore Staking";

        private int _currentStep = 0;
        private List<Func<Task<int>>> _steps = new List<Func<Task<int>>>();
        private Data _data;
        private Settings _settings => _data?.Settings;
        private CancellationTokenSource _cts;
        private string _errorMessage = String.Empty;

        private IRpcClient _client;
        private IStreamingRpcClient _streamingClient;
        private HttpClient _httpClient;
        private List<StakeInformation> _stakeInfo;
        private (int Width, int Height) windowSize = (Console.WindowWidth, Console.WindowHeight);
        private Table _stakingTable;
        private Table _historicalTable;
        private HistoricalStakingData _historicalData = new HistoricalStakingData();

        private string _historicalDataDirectory = Path.Combine(Utils.GetExecutableDirectory(), Settings.StakingViewSettings.Directory);

        public StakingModule()
        {
        }

        public async Task<ExecuteResult> ExecuteAsync(Data data)
        {
            try
            {
                if (_streamingClient?.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    await _streamingClient.DisconnectAsync();
                }
            }
            catch(Exception ex)
            {
                _logger.Log(LogLevel.Warn, $"Failed to disconnect RPC streaming client. Message: {ex.Message}");
            }

            return new ExecuteResult
            {
                Exited = true
            };
        }

        public async Task ExitAsync()
        {
            _cts.Cancel();
        }

        public async Task<(bool, string)> InitializeAsync(Data data)
        {
            _client = ClientFactory.GetClient(data.Settings.RPCSetting.Url);
            _httpClient = new HttpClient()
            {
                Timeout = TimeSpan.FromSeconds(3)
            };
             
            _cts = new CancellationTokenSource();
            _currentStep = 0;
            _data = data;

            if(!await Setup())
            {
                return (false, "Failed to pull required information from RPC");
            }

            Directory.CreateDirectory(_historicalDataDirectory);
            string file = Path.Combine(_historicalDataDirectory, _settings.StakingViewSetting.StakingViewCacheFile);

            if (File.Exists(file))
            {
                try
                {
                    string historicalData = await File.ReadAllTextAsync(file);

                    _historicalData = JsonConvert.DeserializeObject<HistoricalStakingData>(historicalData);
                }
                catch
                {
                    //Ignore
                    _errorMessage = $"[red]Failed to read historical data from cache. Cache was deleted[/]";

                    _logger.Log(LogLevel.Warn, $"Failed to read historical data from cache. Cache was deleted");

                    _historicalData = null;
                }
            }

            if(_historicalData == null)
            {
                _historicalData = new HistoricalStakingData();
            }

            foreach(var stake in _stakeInfo)
            {
                _historicalData.BoostData.TryAdd(stake.Boost.Name, new HistoricalStakingData.BoostRewardData(stake.Boost.CheckpointAddress));
            }

            await SaveHistoricalData();
            _historicalData.Calculate();

            try
            {
                while (true)
                {
                    //Exiting
                    if (!await DisplayOptions())
                    {
                        return (true, String.Empty);
                    }
                }
            }
            catch(OperationCanceledException)
            {
                return (true, String.Empty);
            }
        }

        private async Task<bool> Setup()
        {
            List<BoostInformation> boosts = OreProgram.Boosts;
            (Wallet wallet, string pubKey) = await _settings.GetWalletAsync();

            if (wallet == null && String.IsNullOrEmpty(pubKey))
            {
                return false;
            }

            _stakeInfo = boosts.Select(x => new StakeInformation(x, wallet?.Account.PublicKey ?? new PublicKey(pubKey))).ToList();

            var result = await SendWithRetry(() => _client.GetAccountInfoAsync(OreProgram.BoostConfig, Solnet.Rpc.Types.Commitment.Processed));

            if(result == null || !result.WasSuccessful)
            {
                return false;
            }

            if(result != null)
            {
                BoostConfig directory = BoostConfig.Deserialize(Convert.FromBase64String(result.Result.Value.Data[0]));

                List<PublicKey> newBoosts = new List<PublicKey>();

                for(int i = 0; i < (int)directory.Length; i++)
                {
                    PublicKey boost = directory.Boosts[i];

                    StakeInformation stakeInfo = _stakeInfo.FirstOrDefault(x => x.Boost.BoostAddress.Equals(boost));

                    //New boost, add
                    if(stakeInfo == null)
                    {
                        newBoosts.Add(boost);
                    }
                    else
                    {
                        stakeInfo.Enabled = true;
                    }
                }

                //Will add the boosts in to see stake, but that's it
                if(newBoosts.Count > 0)
                {
                    var boostResults = await SendWithRetry(() => _client.GetMultipleAccountsAsync(newBoosts.Select(x => x.Key).ToList(), Solnet.Rpc.Types.Commitment.Processed));

                    if (boostResults != null)
                    {
                        for (int i = 0; i < boostResults.Result.Value.Count; i++)
                        {
                            var boostInfo = boostResults.Result.Value[i];

                            if(boostInfo == null)
                            {
                                continue;
                            }

                            Boost newBoost = Boost.Deserialize(Convert.FromBase64String(boostInfo.Data[0]));

                            var boostTokenInfoResults = await SendWithRetry(() => _client.GetTokenMintInfoAsync(newBoost.Mint, Solnet.Rpc.Types.Commitment.Processed));

                            if (boostTokenInfoResults != null)
                            {
                                var bInfo = new BoostInformation(newBoost.Mint, boostTokenInfoResults.Result.Value.Data.Parsed.Info.Decimals, "Unknown", BoostInformation.PoolType.Unknown, newBoosts[i], null);

                                _stakeInfo.Add(new StakeInformation(bInfo, wallet?.Account.PublicKey ?? new PublicKey(pubKey)));
                            }
                        }
                    }
                }
            }

            return true;
        }

        private async Task<bool> DisplayOptions()
        {
            if(_cts.IsCancellationRequested)
            {
                return false;
            }

            await UpdateStakeInformation();
            UpdateStakingTable();

            AnsiConsole.Write(_stakingTable);
            AnsiConsole.Write(_historicalTable);

            const string refresh = "Refresh";
            const string liveView = "Live View";
            const string hourlyView = "Hourly Historical Rewards";
            const string historical = "-- Update Historical Rewards";
            const string exit = "Exit";

            SelectionPrompt<string> displayPrompt = new SelectionPrompt<string>();
            displayPrompt.Title(_errorMessage);
            _errorMessage = String.Empty;

            displayPrompt.AddChoice(refresh);
            //displayPrompt.AddChoice(liveView);

            //if (_historicalData.TotalTransactions > 0)
            //{
            //    displayPrompt.AddChoice(hourlyView);
            //}

            //displayPrompt.AddChoice(historical);
            displayPrompt.AddChoice(exit);

            string result = await displayPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

            switch (result)
            {
                case refresh:
                    return true;
                case liveView:
                    await LiveView();
                    return true;
                case hourlyView:
                    await HistoricalHourlyView();
                    return true;
                case historical:
                    await UpdateHistoricalData();
                    return true;
                case exit:
                    return false;
            }

            return false;
        }

        private async Task HistoricalHourlyView()
        {
            StakeInformation stakeChoice = null;

            while (true)
            {
                AnsiConsole.Clear();

                if(stakeChoice != null)
                {
                    GenerateTable(stakeChoice);
                }

                SelectionPrompt<StakeInformation> selectionPrompt = new SelectionPrompt<StakeInformation>();
                selectionPrompt.Title("\nSelect boost to view hourly data");

                selectionPrompt.UseConverter((stakeInfo) =>
                {
                    if(stakeInfo == null)
                    {
                        return $"Exit";
                    }

                    return stakeInfo.Boost.Name;
                });

                selectionPrompt.AddChoices(_stakeInfo);
                selectionPrompt.AddChoice(null);

                stakeChoice = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

                if(stakeChoice == null)
                {
                    return;
                }
            }

            void GenerateTable(StakeInformation stakeInformation)
            {
                if (!_historicalData.BoostData.TryGetValue(stakeInformation.Boost.Name, out var rewardData))
                {
                    return;
                }

                List<Table> tableColumns = new List<Table>();

                foreach(var day in rewardData.Days.OrderByDescending(x => x.DayStart))
                {
                    var date = DateTimeOffset.FromUnixTimeSeconds(day.DayStart).UtcDateTime;

                    Table table = new Table();
                    table.Title($"{stakeInformation.Boost.Name} Boost | {date:M/dd}");
                    table.AddColumns("Date", "Rewards", "Time", "Lock Time");

                    //Add first row
                    double hourlyRewards = (day.TotalRewards / OreProgram.OreDecimals) / TimeSpan.FromSeconds(day.TotalTime).TotalHours;
                    double lostRewards = hourlyRewards * TimeSpan.FromSeconds(day.TotalLockTime).TotalHours;


                    table.AddRow($"Total", 
                        $"{day.TotalRewards/OreProgram.OreDecimals:n11}", 
                        $"{PrettyFormatTime(TimeSpan.FromSeconds(day.TotalTime))}",
                        $"{PrettyFormatTime(TimeSpan.FromSeconds(day.TotalLockTime))}");
                    table.AddRow($"Hourly Rate", $"{hourlyRewards:n11}", "Lost Rewards", $"{lostRewards:n11}");
                    table.AddRow($"[cyan]-----------[/]", "[cyan]------------[/]", "[cyan]------------[/]", "[cyan]------------[/]");

                    foreach (var checkpoint in day.Checkpoints.OrderByDescending(x => x.CheckpointStart))
                    {
                        var checkpointTime = DateTimeOffset.FromUnixTimeSeconds(checkpoint.CheckpointStart).UtcDateTime;

                        table.AddRow($"{checkpointTime:HH:mm:ss}",
                            $"{checkpoint.RewardAmount / OreProgram.OreDecimals:n11}",
                            $"{WrapBooleanColor(PrettyFormatTime(TimeSpan.FromSeconds(checkpoint.TotalTime)), checkpoint.TotalTime < 3720, Color.Green, Color.Red)}",
                            WrapBooleanColor(PrettyFormatTime(TimeSpan.FromSeconds(checkpoint.LockTime)), checkpoint.LockTime < 60, Color.Green, Color.Red));
                    }

                    tableColumns.Add(table);

                    //layout[splitName].Update(panel);;
                }

                Columns columns = new Columns(tableColumns.ToArray());

                AnsiConsole.Write(columns);
            }
        }

        private async Task<bool> UpdateHistoricalData()
        {
            DateTimeOffset currentDate = new DateTimeOffset(DateTime.Today.ToUniversalTime());
            long endTimeUTC = currentDate.AddDays(-_settings.StakingViewSetting.TotalHistoricalDays).ToUnixTimeSeconds();

            await AnsiConsole.Status().StartAsync("Starting signature pulling...", async (context) =>
            {
                ulong limit = 1000;

                #region Signature pulling

                foreach (var boostKvp in _historicalData.BoostData)
                {
                    List<StakeCheckpointTransaction> checkpointTransactions = new List<StakeCheckpointTransaction>();
                    context.Status($"Grabbing transaction signatures for [cyan]{boostKvp.Key}[/] boost. Total: {checkpointTransactions.Count}");

                    var boost = boostKvp.Value;
                    string recentHash = boost.MostRecentHash;
                    string beforeHash = null;
                    bool keepPulling = true;

                    while (keepPulling)
                    {
                        if(_cts.IsCancellationRequested)
                        {
                            return;
                        }

                        var transactions = await SendWithRetry(() => _client.GetSignaturesForAddressAsync(boost.Boost, limit: limit, before: beforeHash, until: recentHash), rateLimitDelay: 5000);

                        if (transactions == null || !transactions.WasSuccessful)
                        {
                            _errorMessage = $"Fail to pull signature information for historical values for [cyan]{boostKvp.Key}[/] boost. Message: {transactions?.Reason}";

                            break;
                        }

                        int totalTransactions = checkpointTransactions.Count;

                        transactions.Result.ForEach(x =>
                        {
                            long time = (long)(x.BlockTime ?? 0);

                            if (time != 0 && time < endTimeUTC)
                            {
                                return;
                            }

                            checkpointTransactions.Add(new StakeCheckpointTransaction
                            {
                                Signature = x.Signature,
                                Timestamp = time
                            });
                        });

                        int increasedTransactions = checkpointTransactions.Count - totalTransactions;

                        context.Status($"Grabbing transaction signatures for [cyan]{boostKvp.Key}[/] boost. Total: {checkpointTransactions.Count}");

                        if (increasedTransactions < (int)limit)
                        {
                            AnsiConsole.MarkupLine($"[green]Finished pulling transaction signature for [cyan]{boostKvp.Key}[/] boost. Total: {checkpointTransactions.Count}[/]");

                            keepPulling = false;
                            continue;
                        }

                        beforeHash = checkpointTransactions.Last().Signature;

                        //Delay Requests by 1s due to rate limits
                        if (_settings.RPCSetting.Provider == Settings.RPCSettings.RPCProvider.Solana)
                        {
                            await Task.Delay(1000);
                        }
                    }

                    // Only keep last x days of data
                    checkpointTransactions.AddRange(boost.TransactionCache.Where(x => x.Timestamp > endTimeUTC));

                    boost.TransactionCache.Clear();
                    boost.TransactionCache.AddRange(checkpointTransactions);

                    //Save current cache
                    await SaveHistoricalData();
                }

                #endregion

            });

            var totalTransactions = _historicalData.TotalTransactionsToPull;
            var archivalTransactions = _historicalData.GetArchivalTransactions(DateTimeOffset.UtcNow.AddDays(-2));
            var normalTransactions = totalTransactions - archivalTransactions;

            switch (_settings.RPCSetting.Provider)
            {
                case Settings.RPCSettings.RPCProvider.Solana:
                    AnsiConsole.MarkupLine($"\nDefault solana RPC has heavy rate limits resulting in slow transaction pulling. Transactions: {totalTransactions}. Over 2 days old: {archivalTransactions}");
                    break;
                case Settings.RPCSettings.RPCProvider.Helius:
                    AnsiConsole.MarkupLine($"\nHelius will use ~{normalTransactions + archivalTransactions * 10} credits. Grabbing archival transactions start at around 2 days old with 10x cost of normal transactions. Norma; {normalTransactions}. Archival: {archivalTransactions}");
                    break;
                case Settings.RPCSettings.RPCProvider.Quicknode:
                    AnsiConsole.MarkupLine($"\nQuicknode will use ~{totalTransactions * 30} credits");
                    break;
                case Settings.RPCSettings.RPCProvider.Unknown:
                    AnsiConsole.MarkupLine($"\nUnknown RPC provider found. No credit estimates are possible. Total Transactions: {totalTransactions}. Over 2 days old: {archivalTransactions}");
                    break;
            }

            if(totalTransactions == 0)
            {
                return true;
            }

            if(await AnsiConsole.AskAsync($"Continue (y/n)?", "y", _cts.Token) != "y")
            {
                return false;
            }

            bool hasFailures = false;

            await AnsiConsole.Status().StartAsync("Starting transaction pulling...", async (context) =>
            {
                #region Transaction pulling

                foreach (var boostKvp in _historicalData.BoostData)
                {
                    var boost = boostKvp.Value;
                    int failedTransactions = 0;
                    int consecutiveFailures = 0;

                    var boostTotalTransactions = boostKvp.Value.TransactionCache.Count(x => !x.DataPulled);
                    Stopwatch sw = Stopwatch.StartNew();
                    TimeSpan lastSave = sw.Elapsed;

                    int counter = 0;

                    for (int i = 0; i < boost.TransactionCache.Count; i++)
                    {
                        if (_cts.IsCancellationRequested)
                        {
                            return;
                        }

                        TimeSpan eta = TimeSpan.Zero;

                        if(counter > 0)
                        {
                            int remainingTransactions = boostTotalTransactions - counter;

                            //Calculating remaining time based on transaction pulls
                            double remaining = remainingTransactions / (double)counter;

                            eta = TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds * remaining);
                        }

                        context.Status($"Pulling transaction data for [cyan]{boostKvp.Key}[/]. This can take awhile. Complete: {i}/{boostTotalTransactions}. Failed: {WrapBooleanColor(failedTransactions.ToString(), failedTransactions > 0, Color.Red, null)}. " +
                            $"ETA: {PrettyFormatTime(eta)}. Last Save: {PrettyFormatTime(sw.Elapsed - lastSave)} ago");

                        StakeCheckpointTransaction transaction = boost.TransactionCache[i];

                        if (transaction.DataPulled)
                        {
                            continue;
                        }

                        var transactionResult = await SendWithRetry(() => _client.GetTransactionAsync(transaction.Signature), rateLimitDelay: _settings.RPCSetting.Provider == Settings.RPCSettings.RPCProvider.Solana ? 5000 : 1000);

                        if (!transactionResult.WasSuccessful)
                        {
                            //Only show 10 failed to keep things clean
                            if (failedTransactions < 10)
                            {
                                AnsiConsole.MarkupLine($"[yellow]Failed to pull transaction '{transaction.Signature}'. Reason: {transactionResult.Reason}[/]");
                            }
                            hasFailures = true;
                            ++failedTransactions;
                            ++consecutiveFailures;

                            if(consecutiveFailures > 10)
                            {
                                AnsiConsole.MarkupLine($"[yellow]Constant failures. Most likely RPC does not have historical transactions[/]");

                                break;
                            }

                            continue;
                        }

                        consecutiveFailures = 0;

                        TransactionInfo transactionInfo = transactionResult.Result.Transaction;
                        List<PublicKey> accountKeys = transactionInfo.Message.AccountKeys.Select(x => new PublicKey(x)).ToList();

                        //Transaction failed, so safe to ignore
                        if(transactionResult.Result.Meta.Error != null)
                        {
                            transaction.DataPulled = true;
                            continue;
                        }

                        Base58Encoder encoder = new Base58Encoder();

                        bool isCheckpointTransaction = false;

                        for (int x = 0; x < transactionInfo.Message.Instructions.Length; x++)
                        {
                            InstructionInfo instruction = transactionInfo.Message.Instructions[x];

                            //No need to continue checking
                            if (isCheckpointTransaction)
                            {
                                break;
                            }

                            PublicKey key = accountKeys[instruction.ProgramIdIndex];

                            //Not boost instruction
                            if (!key.Equals(OreProgram.BoostProgramId))
                            {
                                continue;
                            }

                            //Data is empty
                            if (instruction.Data.Length == 0)
                            {
                                continue;
                            }

                            byte[] data = encoder.DecodeData(instruction.Data);

                            //Not a boost rebase
                            if (data[0] != 0x3)
                            {
                                continue;
                            }

                            isCheckpointTransaction = true;

                            InnerInstruction innerInstruction = transactionResult.Result.Meta.InnerInstructions.FirstOrDefault(z => z.Index == x);

                            if (innerInstruction != null)
                            {
                                //Parse out reward
                                foreach(InstructionInfo innerInstructionInfo in innerInstruction.Instructions)
                                {
                                    PublicKey innerProgramId = accountKeys[innerInstructionInfo.ProgramIdIndex];

                                    //Ore program
                                    if(innerProgramId.Equals(OreProgram.ProgramId))
                                    {
                                        if(innerInstructionInfo.Data.Length > 0)
                                        {
                                            byte[] innerData = encoder.DecodeData(innerInstructionInfo.Data);

                                            //Claim command
                                            if (innerData.Length == 9 && innerData[0] == 0x00)
                                            {
                                                transaction.RewardAmount = BinaryPrimitives.ReadUInt64LittleEndian(innerData[1..]);
                                            }
                                        }
                                    }
                                }
                            }

                            transaction.DataPulled = true;
                        }

                        counter++;

                        if ((sw.Elapsed - lastSave).TotalSeconds > 10)
                        {
                            await SaveHistoricalData();
                            lastSave = sw.Elapsed;
                        }

                        //Delay Requests by 1s due to rate limits
                        if (_settings.RPCSetting.Provider == Settings.RPCSettings.RPCProvider.Solana)
                        {
                            await Task.Delay(1000);
                        }
                    }

                    AnsiConsole.MarkupLine($"[green]Pulling transaction data for [cyan]{boostKvp.Key}[/] finished. Complete: {boostTotalTransactions}. Failed: {WrapBooleanColor(failedTransactions.ToString(), failedTransactions > 0, Color.Red, null)}. " +
                            $"Time: {PrettyFormatTime(sw.Elapsed)}[/]");

                    await SaveHistoricalData();
                }
                #endregion
            });

            _historicalData.Calculate();

            if (_cts.IsCancellationRequested)
            {
                return false;
            }

            if(hasFailures)
            {
                await AnsiConsole.AskAsync<string>($"Press enter to continue", "", _cts.Token);
            }

            return true;
        }

        private async Task<bool> SaveHistoricalData()
        {
            try
            {
                string data = JsonConvert.SerializeObject(_historicalData);
                await File.WriteAllTextAsync(Path.Combine(_historicalDataDirectory, _settings.StakingViewSetting.StakingViewCacheFile), data);

                return true;
            }
            catch(Exception)
            {
                _logger.Log(LogLevel.Warn, $"Failed to save historical data");

                return false;
            }
        }

        private async Task<bool> LiveView()
        {
            Table messageTable = new Table();
            messageTable.Border(TableBorder.Square);
            messageTable.AddColumns("Time", "Updates");

            AnsiConsole.MarkupLine($"\n[green]Subscribing to boost accounts...[/]");

            if (!await InitializeClient(_stakingTable, messageTable))
            {
                return false;
            }

            Layout liveLayout = new Layout("Root").SplitRows(
                new Layout("staking", _stakingTable),
                new Layout("messages", messageTable));
            liveLayout.Ratio = 10;
            liveLayout["staking"].Ratio = 4;
            liveLayout["messages"].Ratio = 6;

            bool redraw = true;

            while (redraw && !_cts.IsCancellationRequested)
            {
                AnsiConsole.Clear();

                await AnsiConsole.Live(liveLayout).StartAsync(async (ctx) =>
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        if (_cts.IsCancellationRequested)
                        {
                            redraw = false;

                            return;
                        }

                        if(WindowSizeChange())
                        {
                            return;
                        }

                        UpdateStakingTable();
                        ctx.UpdateTarget(liveLayout);

                        await Task.Delay(1000);
                    }
                });
            }

            return false;
        }

        private async Task<bool> InitializeClient(Table stakingTable, Table messageTable)
        {
            try
            {
                if (_streamingClient != null && _streamingClient.State == WebSocketState.Open)
                {
                    try
                    {
                        _streamingClient.ConnectionStateChangedEvent -= _streamingClient_ConnectionStateChangedEvent;
                        await _streamingClient.DisconnectAsync();
                    }
                    catch
                    {
                        //Ignore
                    }
                }

                _streamingClient = ClientFactory.GetStreamingClient(_data.Settings.RPCSetting.Url.Replace("http", "ws"));
                _streamingClient.ConnectionStateChangedEvent += _streamingClient_ConnectionStateChangedEvent;
                await _streamingClient.ConnectAsync();

                List<SubscriptionState> states = new List<SubscriptionState>();
                List<string> boostsWatching = new List<string>();

                foreach (var stakingInfo in _stakeInfo.Where(x => x.UserStake > 0))
                {
                    boostsWatching.Add(stakingInfo.Boost.Name);

                    //Stake Account
                    states.Add(await _streamingClient.SubscribeAccountInfoAsync(stakingInfo.StakeAccount, (state, info) =>
                    {
                        if (info == null)
                        {
                            return;
                        }

                        Stake stake = Stake.Deserialize(Convert.FromBase64String(info.Value.Data[0]));

                        double oldUserStake = stakingInfo.UserStake;
                        double oldRewards = stakingInfo.Rewards;

                        stakingInfo.UserStake = stake.Balance / Math.Pow(10, stakingInfo.Boost.Decimal);
                        stakingInfo.Rewards = stake.CalculateRewards(stakingInfo.BoostInfo, stakingInfo.TotalStakers) / OreProgram.OreDecimals;

                        if(stakingInfo.Rewards > oldRewards)
                        {
                            AddMessage($"[[{stakingInfo.Boost.Name}]] Received [green]{stakingInfo.Rewards - oldRewards:0.00000000000}[/] ORE for the hour");
                        }
                    }));

                    //Checkpoint account
                    states.Add(await _streamingClient.SubscribeAccountInfoAsync(stakingInfo.Boost.CheckpointAddress, (state, info) =>
                    {
                        if (info == null)
                        {
                            return;
                        }

                        CheckPoint checkpoint = CheckPoint.Deserialize(Convert.FromBase64String(info.Value.Data[0]));

                        stakingInfo.LastPayout = checkpoint.LastCheckPoint.UtcDateTime;

                    }));

                    //Proof account for pending
                    states.Add(await _streamingClient.SubscribeAccountInfoAsync(stakingInfo.Boost.BoostProof, (state, info) =>
                    {
                        if (info == null)
                        {
                            return;
                        }

                        Proof proof = Proof.Deserialize(Convert.FromBase64String(info.Value.Data[0]));

                    }));

                    DateTime lockStart = default;

                    //Boost account
                    states.Add(await _streamingClient.SubscribeAccountInfoAsync(stakingInfo.Boost.BoostAddress, (state, info) =>
                    {
                        if (info == null)
                        {
                            return;
                        }

                        Boost boost = Boost.Deserialize(Convert.FromBase64String(info.Value.Data[0]));

                        double oldMultiplier = Math.Round(stakingInfo.Multiplier, 2);
                        bool oldLocked = stakingInfo.Locked;

                        stakingInfo.TotalBoostStake = boost.TotalDeposits / Math.Pow(10, stakingInfo.Boost.Decimal);
                        stakingInfo.TotalStakers = boost.TotalStakers;
                        stakingInfo.Multiplier = Math.Round(boost.Multiplier / 1000.0, 2);
                        stakingInfo.BoostInfo = boost;
                        stakingInfo.Locked = false;

                        if(oldLocked != stakingInfo.Locked)
                        {
                            if(stakingInfo.Locked)
                            {
                                lockStart = DateTime.Now;
                            }

                            //AddMessage($"[[{stakingInfo.Boost.Name}]] Boost has been {(boost.Locked > 0 ? $"[red]locked[/] to initiate payouts" : $"[green]unlocked[/] to receive rewards. Checkpoint time: {PrettyFormatTime(DateTime.Now - lockStart)}")}");
                        }

                        if(oldMultiplier != stakingInfo.Multiplier)
                        {
                            //AddMessage($"[[{stakingInfo.Boost.Name}]] Multiplier has been changed to [cyan]{stakingInfo.Multiplier:0.00}x[/]");
                        }

                        //TODO: seconds, new table with overall (profit, estimates, lo0ck time, checkpoint)
                        //if (oldUserPendingRewards < stakingInfo.UserPendingRewards)
                        //{
                        //    AddMessage($"[[{stakingInfo.Boost.Name}]] Received [yellow]{stakingInfo.UserPendingRewards - oldUserPendingRewards:0.00000000000}[/] ore as pending rewards");
                        //}
                    }));
                }

                int i = 0;

                while(states.Any(x => x.State != SubscriptionStatus.Subscribed))
                {
                    if(i == 5)
                    {
                        _errorMessage = $"[red]Failed to subscribe to all accounts[/]";
                        return false;
                    }

                    if(states.Any(x => x.State == SubscriptionStatus.ErrorSubscribing))
                    {
                        _errorMessage = $"[red]Failed to subscribe to all accounts with error '{states.FirstOrDefault(x => !String.IsNullOrEmpty(x.LastError))?.LastError}'[/]";
                        return false;
                    }

                    await Task.Delay(1000);

                    ++i;
                }

                AddMessage($"[green]Live updates for boosts {String.Join(", ", boostsWatching)} has started[/]");

                return true;
            }
            catch (Exception ex)
            {
                _errorMessage = $"\n[red]Error: Failed to connect to streaming client. Message: {ex.Message}[/]";
            }

            return false;


            void AddMessage(string message)
            {
                messageTable.InsertRow(0, DateTime.Now.ToString("MM/dd HH:mm"), message);

                if (messageTable.Rows.Count > 20)
                {
                    messageTable.Rows.RemoveAt(messageTable.Rows.Count - 1);
                }
            }

            async void _streamingClient_ConnectionStateChangedEvent(object? sender, WebSocketState e)
            {
                if (e == WebSocketState.Aborted || e == WebSocketState.Closed)
                {
                    AddMessage($"[yellow]Disconnected from streaming client. Reason: {e}[/]");

                    while (!await InitializeClient(stakingTable, messageTable) && !_cts.IsCancellationRequested)
                    {
                        const int totalSeconds = 5;

                        AddMessage($"[yellow]Failed to reconnect. Will attempt in {totalSeconds}s[/]");
                        await Task.Delay(totalSeconds * 1000);
                    }
                }
            }
        }

        private void UpdateStakingTable()
        {
            var oreBoost = _stakeInfo.FirstOrDefault(x => x.Boost.Type == BoostInformation.PoolType.Ore);

            if(_stakingTable == null || WindowSizeChange())
            {
                _stakingTable = new Table();
                _stakingTable.Title($"Staking Information [[Ore: ${oreBoost.OreUSDValue:0.00}]]");
                _stakingTable.AddColumns("LP", "Mult", "Stakers", "Total Stake", "Relative Yield", "User Stake", "Share", "Rewards");
                _stakingTable.ShowRowSeparators = true;
                foreach(var column in _stakingTable.Columns)
                {
                    column.RightAligned();
                }

                foreach (var stakeInfo in _stakeInfo)
                {
                    var nextPayoutTime = (stakeInfo.LastPayout.AddHours(1) - DateTime.UtcNow);

                    //Calculate relative reward rate
                    var relativeProfit = stakeInfo.Multiplier == 0 || stakeInfo.BoostTotalUSDValue  == 0? 0 : (double)oreBoost.BoostTotalUSDValue / ((double)stakeInfo.BoostTotalUSDValue / stakeInfo.Multiplier);


                    _stakingTable.AddRow(WrapBooleanColor(stakeInfo.Boost.Name, stakeInfo.Enabled, null, Color.Red),
                                        $"{stakeInfo.Multiplier:0.##}x",
                                        stakeInfo.TotalStakers.ToString(),
                                        $"{stakeInfo.TotalBoostStake:0.###} (${stakeInfo.BoostTotalUSDValue:n2})",
                                        $"{relativeProfit:0.00}x",
                                        $"{stakeInfo.UserStake:0.###} (${stakeInfo.UserStakeUSDValue:n2})", 
                                        $"{stakeInfo.SharePercent:0.####}%",
                                        WrapBooleanColor($"{stakeInfo.Rewards:n11} (${stakeInfo.RewardUSDValue:n2})", stakeInfo.Rewards > 0, Color.Green, null)
                                        );
                }
            }
            else
            {
                _stakingTable.Title($"Staking Information [[Ore: ${oreBoost.OreUSDValue:0.00}]]");
                
                for(int i = 0; i < _stakeInfo.Count; i++)
                {
                    var stakeInfo = _stakeInfo[i];
                    var nextPayoutTime = (stakeInfo.LastPayout.AddHours(1) - DateTime.UtcNow);

                    //Calculate relative reward rate
                    var relativeProfit = (double)oreBoost.BoostTotalUSDValue / ((double)stakeInfo.BoostTotalUSDValue / stakeInfo.Multiplier);


                    _stakingTable.UpdateCell(i, 1, $"{stakeInfo.Multiplier:0.##}x");
                    _stakingTable.UpdateCell(i, 2, stakeInfo.TotalStakers.ToString());
                    _stakingTable.UpdateCell(i, 3, $"{stakeInfo.TotalBoostStake:0.###} (${stakeInfo.BoostTotalUSDValue:n2})");
                    _stakingTable.UpdateCell(i, 4, $"{relativeProfit:0.00}x");
                    _stakingTable.UpdateCell(i, 5, $"{stakeInfo.UserStake:0.###} (${stakeInfo.UserStakeUSDValue:n2})");
                    _stakingTable.UpdateCell(i, 6, $"{stakeInfo.SharePercent:0.####}%");
                    _stakingTable.UpdateCell(i, 7, WrapBooleanColor($"{stakeInfo.Rewards:n11} (${stakeInfo.RewardUSDValue:n2})", stakeInfo.Rewards > 0, Color.Green, null));
                }
            }

            //Lazy, recreate very time
            _historicalTable = new Table();
            _historicalTable.Title("Historical Data");
            _historicalTable.AddColumn("Date (remaining time)");
            _historicalTable.AddColumns(_stakeInfo.Select(x => $"{x.Boost.Name} [[lock time]]").ToArray());

            //Add rows
            foreach(var day in _historicalData.BoostData.SelectMany(x => x.Value.Days).GroupBy(x => x.DayStart).OrderByDescending(x => x.Key))
            {
                var currentDay = DateTime.UtcNow.Date;
                var firstDay = DateTimeOffset.FromUnixTimeSeconds(day.Key).LocalDateTime;

                var remainingTime = currentDay.AddDays(1) - DateTime.UtcNow;

                List<string> rowData = new List<string> { $"{firstDay:M/d}{(currentDay == firstDay ? $" ({PrettyFormatTime(remainingTime)} left)" : String.Empty)}" };

                foreach(var stakeInfo in _stakeInfo)
                {
                    //Find day information
                    var dayInfo = day.FirstOrDefault(x => x.BoostName == stakeInfo.Boost.CheckpointAddress);

                    rowData.Add(dayInfo == null ? "N/A" : $"{dayInfo.TotalRewards / OreProgram.OreDecimals:n11} ore [red][[{PrettyFormatTime(TimeSpan.FromSeconds(dayInfo.TotalLockTime))}]][/]");
                }

                _historicalTable.AddRow(rowData.ToArray());
            }
        }

        private string PrettyFormatTime(TimeSpan span)
        {
            string formatted = String.Format("{0}{1}{2}{3}",
                span.Duration().Days > 0 ? String.Format("{0:0}d ", span.Days) : String.Empty,
                span.Duration().Hours > 0 ? String.Format("{0:0}h ", span.Hours) : String.Empty,
                span.Duration().Minutes > 0 ? String.Format("{0:0}m ", span.Minutes) : String.Empty,
                span.Duration().Seconds > 0 ? String.Format("{0:0}s", span.Seconds) : String.Empty);

            if (formatted.EndsWith(", "))
            {
                formatted = formatted.Substring(0, formatted.Length - 2);
            }

            if (String.IsNullOrEmpty(formatted))
            {
                formatted = "0s";
            }

            return formatted;
        }

        private async Task UpdateStakeInformation()
        {
            AnsiConsole.Clear();

            await AnsiConsole.Status().StartAsync($"Updating staking info", async ctx =>
            {
                if(await UpdateBoostInformation())
                {
                    AnsiConsole.MarkupLine($"[green]Successfully updated staking info[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]Failed to update staking info[/]");
                }

                ctx.Status("Updating Kamino pool info ...");

                if(await UpdateKaminoPoolInfo(_stakeInfo.Where(x => x.Boost.Type == BoostInformation.PoolType.Kamino)))
                {
                    AnsiConsole.MarkupLine($"[green]Successfully updated kamino pool info[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]Failed to update kamino pool info[/]");
                }

                ctx.Status("Updating Meteroa pool info ...");

                if (await UpdateMeteroaPool(_stakeInfo.Where(x => x.Boost.Type == BoostInformation.PoolType.Meteora)))
                {
                    AnsiConsole.MarkupLine($"[green]Successfully updated meteroa pool info[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]Failed to update meteroa pool info[/]");
                }

                ctx.Status("Grabbing prices from Coin Gecko...");

                if(await UpdatePrices())
                {
                    AnsiConsole.MarkupLine($"[green]Successfully updated prices[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]Failed to update prices from Coin Gecko[/]");
                }
            });

            AnsiConsole.Clear();
        }

        private async Task<bool> UpdatePrices()
        {
            try
            {
                HashSet<string> coingeckoids = _stakeInfo.Where(x => !String.IsNullOrEmpty(x.Boost.CoinGeckoName)).Select(x => x.Boost.CoinGeckoName).ToHashSet();

                string url = String.Format("https://api.coingecko.com/api/v3/simple/price?ids={0}&vs_currencies=usd", String.Join(",", coingeckoids));

                string data = await _httpClient.GetStringAsync(url);

                var priceData = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, decimal>>>(data);

                foreach(var s in _stakeInfo)
                {
                    if(priceData.TryGetValue("ore", out var v))
                    {
                        s.OreUSDValue = v["usd"];
                    }

                    if (!String.IsNullOrEmpty(s.Boost.CoinGeckoName))
                    {
                        if (priceData.TryGetValue(s.Boost.CoinGeckoName, out v))
                        {
                            s.TokenBUSDValue = v["usd"];
                        }
                    }
                }

                return true;
            }
            catch(Exception ex)
            {
                _logger.Log(LogLevel.Warn, $"Failed to pull prices from Coin Gecko. Message: {ex.Message}");

                return false;
            }
        }

        private async Task<bool> UpdateBoostInformation()
        {
            List<string> accounts = new List<string>();

            const int baseAccountSize = 5;

            foreach(var info in _stakeInfo)
            {
                accounts.Add(info.Boost.BoostAddress);
                accounts.Add(info.StakeAccount);
                accounts.Add(info.Boost.BoostProof);
                accounts.Add(info.Boost.CheckpointAddress);
                accounts.Add(info.Boost.MintAddress);

                if (info.Boost.ExtraData != null)
                {
                    accounts.Add(info.Boost.ExtraData.LPVaultA);
                    accounts.Add(info.Boost.ExtraData.LPVaultB);
                    accounts.Add(info.Boost.ExtraData.TokenVaultA);
                    accounts.Add(info.Boost.ExtraData.TokenVaultB);
                    accounts.Add(info.Boost.ExtraData.LPAMint);
                    accounts.Add(info.Boost.ExtraData.LPBMint);
                }
            }

            var result = await SendWithRetry(() => _client.GetMultipleAccountsAsync(accounts, Solnet.Rpc.Types.Commitment.Confirmed));

            if(result == null)
            {
                return false;
            }

            List<AccountInfo> accountInfoList = new List<AccountInfo>();

            if (result.WasSuccessful)
            {
                accountInfoList.AddRange(result.Result.Value);
            }
            else if (result.HttpStatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                //Quicknode free tier only allows 5 per call
                const int limitPerCall = 5;

                for (int x = 0; x <= accounts.Count / limitPerCall; x++)
                {
                    var ll = accounts.Skip(x * limitPerCall).Take(limitPerCall).ToList();

                    //Easier
                    if(ll.Count == 0)
                    {
                        break;
                    }

                    var quickNodeResult = await SendWithRetry(() => _client.GetMultipleAccountsAsync(ll, Solnet.Rpc.Types.Commitment.Confirmed));

                    if (quickNodeResult.WasSuccessful)
                    {
                        accountInfoList.AddRange(quickNodeResult.Result.Value);
                    }
                    else
                    {
                        _errorMessage = $"[red]Failed to pull account information. Reason: {quickNodeResult.Reason}[/]";

                        return false;
                    }
                }
            }
            else
            {
                _errorMessage = $"[red]Failed to pull account information. Reason: {result.Reason}[/]";
                return false;
            }

            int accountCount = 0;
            int i = 0;

            while(accountCount < accounts.Count)
            {
                var stakeInfo = _stakeInfo[i++];

                int totalAccountsToRead = baseAccountSize + (stakeInfo.Boost.ExtraData == null ? 0 : BoostInformation.MeteoraExtraData.TotalAccounts);

                var tAccounts = accountInfoList.Skip(accountCount).Take(totalAccountsToRead).ToList();

                accountCount += totalAccountsToRead;

                var boostData = tAccounts[0];
                var stakeData = tAccounts[1];
                var proofData = tAccounts[2];
                var checkpointData = tAccounts[3];
                var mintAccountData = tAccounts[4];

                BoostInformation.PoolType poolType = stakeInfo.Boost.Type;

                //Meteora has slow updates for their API, so pulling on-chain data
                if(poolType == BoostInformation.PoolType.Meteora)
                {
                    var lpVaultA = tAccounts[5];
                    var lpVaultB = tAccounts[6];
                    var tokenVaultA = tAccounts[7];
                    var tokenVaultB = tAccounts[8];
                    var lpAMint = tAccounts[9];
                    var lpBMint = tAccounts[10];

                    var mintData = Convert.FromBase64String(mintAccountData.Data[0]); //LP pair token
                    var lpVaultAData = Convert.FromBase64String(lpVaultA.Data[0]);  //LP Token A Vault
                    var lpVaultBData = Convert.FromBase64String(lpVaultB.Data[0]);  //LP Token B Vault
                    var tokenVaultAData = Convert.FromBase64String(tokenVaultA.Data[0]); //Token A Vault
                    var tokenVaultBData = Convert.FromBase64String(tokenVaultB.Data[0]); //Token B Vault
                    var lpAMintData = Convert.FromBase64String(lpAMint.Data[0]); //LP A Mint
                    var lpBMintData = Convert.FromBase64String(lpBMint.Data[0]); //LP B Mint

                    double mintAmount = new ReadOnlySpan<byte>(mintData).GetU64(36) / Math.Pow(10, stakeInfo.Boost.Decimal);
                    ulong lpASupply = new ReadOnlySpan<byte>(lpAMintData).GetU64(36);
                    ulong lpBSupply = new ReadOnlySpan<byte>(lpBMintData).GetU64(36);
                    ulong aVaultAmount = new ReadOnlySpan<byte>(lpVaultAData).GetU64(64);
                    ulong bVaultAmount = new ReadOnlySpan<byte>(lpVaultBData).GetU64(64);

                    ulong tokenAAmount = stakeInfo.Boost.ExtraData.CalculateTokenAmount(aVaultAmount, lpASupply, tokenVaultAData);
                    ulong tokenBAmount = stakeInfo.Boost.ExtraData.CalculateTokenAmount(bVaultAmount, lpBSupply, tokenVaultBData);

                    //Assuming ore is first

                    stakeInfo.TotalLPStake = (decimal)mintAmount;
                    stakeInfo.TotalOreStaked = (decimal)(tokenAAmount / OreProgram.OreDecimals);
                    stakeInfo.TotalTokenBStaked = (decimal)(tokenBAmount / Math.Pow(10, stakeInfo.Boost.ExtraData.TokenBDecimal));
                }

                if(boostData != null)
                {
                    Boost boost = Boost.Deserialize(Convert.FromBase64String(boostData.Data[0]));

                    stakeInfo.TotalBoostStake = boost.TotalDeposits / Math.Pow(10, stakeInfo.Boost.Decimal);
                    stakeInfo.TotalStakers = boost.TotalStakers;
                    stakeInfo.Multiplier = boost.Multiplier / 1000.0;
                    stakeInfo.BoostInfo = boost;
                    //stakeInfo.Locked = boost.Locked > 0;

                    if(poolType == BoostInformation.PoolType.Ore)
                    {
                        stakeInfo.TotalOreStaked = (decimal)stakeInfo.TotalBoostStake;
                        stakeInfo.TotalLPStake = (decimal)stakeInfo.TotalBoostStake;
                    }
                }

                if(proofData != null)
                {
                    Proof proof = Proof.Deserialize(Convert.FromBase64String(proofData.Data[0]));

                    stakeInfo.ProofBalance = proof.Balance;
                }

                if (checkpointData != null)
                {
                    CheckPoint checkpoint = CheckPoint.Deserialize(Convert.FromBase64String(checkpointData.Data[0]));

                    stakeInfo.LastPayout = checkpoint.LastCheckPoint.UtcDateTime;
                }

                if(stakeData != null)
                {
                    Stake stake = Stake.Deserialize(Convert.FromBase64String(stakeData.Data[0]));

                    stakeInfo.UserStake = stake.Balance / Math.Pow(10, stakeInfo.Boost.Decimal);
                    stakeInfo.Rewards = stake.Rewards / OreProgram.OreDecimals;
                    stakeInfo.Rewards = stake.CalculateRewards(stakeInfo.BoostInfo, stakeInfo.ProofBalance) / OreProgram.OreDecimals;
                }
                else
                {
                    stakeInfo.UserStake = 0;
                    //stakeInfo.PendingStake = 0;
                    stakeInfo.Rewards = 0;
                }
            }

            return true;
        }

        private async Task<bool> UpdateKaminoPoolInfo(IEnumerable<StakeInformation> boostsToUpdate)
        {
            //No need to update
            if(boostsToUpdate.Count() == 0 || !boostsToUpdate.Any(x => x.LastUpdated.AddMinutes(PoolUpdateDelayMin) <= DateTime.UtcNow))
            {
                return true;
            }

            try
            {
                string data = await _httpClient.GetStringAsync(KaminoPoolUrl);

                List<KaminoPoolData> poolData = JsonConvert.DeserializeObject<List<KaminoPoolData>>(data);

                foreach(StakeInformation boost in boostsToUpdate)
                {
                    KaminoPoolData boostData = poolData.FirstOrDefault(x => x.Strategy == boost.Boost.PoolAddress);

                    if(boostData == null)
                    {
                        continue;
                    }

                    boost.LastUpdated = DateTime.UtcNow;
                    boost.OreUSDValue = boostData.TokenAMint == OreProgram.MintId.Key ? boostData.VaultBalances_.TokenA.TokenUSDValue : boostData.VaultBalances_.TokenB.TokenUSDValue;
                    boost.TokenBUSDValue = boostData.TokenAMint != OreProgram.MintId.Key ? boostData.VaultBalances_.TokenA.TokenUSDValue : boostData.VaultBalances_.TokenB.TokenUSDValue;
                    boost.TotalOreStaked = boostData.TokenAMint == OreProgram.MintId.Key ? boostData.VaultBalances_.TokenA.Total : boostData.VaultBalances_.TokenB.Total;
                    boost.TotalTokenBStaked = boostData.TokenAMint != OreProgram.MintId.Key ? boostData.VaultBalances_.TokenA.Total : boostData.VaultBalances_.TokenB.Total;
                    boost.TotalLPStake = boostData.SharesIssued;

                    //boost.TokenB = boostData.TokenAMint != OreProgram.MintId.Key ? new PublicKey(boostData.TokenAMint) : new PublicKey(boostData.TokenBMint);
                }

                return true;
            }
            catch(Exception ex)
            {
                _logger.Log(LogLevel.Warn, $"Failed to grab kamino LP info. Ex: {ex.Message}");

                return false;
            }
        }

        private async Task<bool> UpdateMeteroaPool(IEnumerable<StakeInformation> boostsToUpdate)
        {
            //No need to update
            if (boostsToUpdate.Count() == 0 || !boostsToUpdate.Any(x => x.LastUpdated.AddMinutes(PoolUpdateDelayMin) <= DateTime.UtcNow))
            {
                return true;
            }

            foreach (StakeInformation boost in boostsToUpdate)
            {
                try
                {
                    string data = await _httpClient.GetStringAsync(String.Format(MeteroaPoolUrl, boost.Boost.PoolAddress));

                    List<MeteroaPoolInfo> poolData = JsonConvert.DeserializeObject<List<MeteroaPoolInfo>>(data);

                    if(poolData != null && poolData.Count > 0)
                    {
                        MeteroaPoolInfo poolInfo = poolData[0];

                        boost.LastUpdated = DateTime.UtcNow;

                        boost.OreUSDValue = poolInfo.PoolTokenMints[0] == OreProgram.MintId.Key ? (poolInfo.PoolTokenUsdAmounts[0] / poolInfo.PoolTokenAmounts[0]) : (poolInfo.PoolTokenUsdAmounts[1] / poolInfo.PoolTokenAmounts[1]);
                        //boost.TokenB = poolInfo.PoolTokenMints[0] != OreProgram.MintId.Key ? new PublicKey(poolInfo.PoolTokenMints[0]) : new PublicKey(poolInfo.PoolTokenMints[1]);
                        boost.TokenBUSDValue = poolInfo.PoolTokenMints[0] != OreProgram.MintId.Key ? (poolInfo.PoolTokenUsdAmounts[0] / poolInfo.PoolTokenAmounts[0]) : (poolInfo.PoolTokenUsdAmounts[1] / poolInfo.PoolTokenAmounts[1]);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log(LogLevel.Warn, $"Failed to grab meteroa LP info for {boost.Boost.Name}. Ex: {ex.Message}");

                    return false;
                }
            }

            return true;
        }

        private async Task<RequestResult<T>> SendWithRetry<T>(Func<Task<RequestResult<T>>> request, int retryCount = 5, int rateLimitDelay = 1000)
        {
            RequestResult<T> lastResult = default;

            for (int i =0; i < retryCount; i++)
            {
                if(_cts.IsCancellationRequested)
                {
                    return lastResult;
                }

                lastResult = await request();

                if(lastResult.WasSuccessful)
                {
                    return lastResult;
                }

                if(lastResult.HttpStatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    await Task.Delay(rateLimitDelay, _cts.Token);

                    continue;
                }

                return lastResult;
            }

            return lastResult;
        }

        private bool WindowSizeChange()
        {
            if (Console.WindowHeight != windowSize.Height || Console.WindowWidth != windowSize.Width)
            {
                windowSize = (Console.WindowWidth, Console.WindowHeight);
                return true;
            }

            return false;
        }

        private string WrapBooleanColor(string text, bool result, Color? successColor, Color? failedColor)
        {
            if(result && successColor.HasValue)
            {
                return $"[{successColor}]{text}[/]";
            }
            else if (!result && failedColor.HasValue)
            {
                return $"[{failedColor}]{text}[/]";
            }

            return text;
        }
    }
}
