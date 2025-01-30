using Blake2Sharp;
using Equix;
using Hardware.Info;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime.OpenCL;
using Newtonsoft.Json;
using NLog;
using Org.BouncyCastle.Asn1.X509.Qualified;
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
using Solnet.Wallet;
using Solnet.Wallet.Bip39;
using Solnet.Wallet.Utilities;
using Spectre.Console;
using Spectre.Console.Rendering;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
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

        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
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

            await Setup();

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

        private async Task Setup()
        {
            List<BoostInformation> boosts = OreProgram.Boosts;
            (Wallet wallet, string pubKey) = await _settings.GetWalletAsync();

            if (wallet == null && String.IsNullOrEmpty(pubKey))
            {
                return;
            }

            _stakeInfo = boosts.Select(x => new StakeInformation(x, wallet?.Account.PublicKey ?? new PublicKey(pubKey))).ToList();

            var result = await SendWithRetry(() => _client.GetAccountInfoAsync(OreProgram.BoostDirectoryId, Solnet.Rpc.Types.Commitment.Processed));

            if(result != null)
            {
                BoostDirectory directory = BoostDirectory.Deserialize(Convert.FromBase64String(result.Result.Value.Data[0]));

                List<PublicKey> newBoosts = new List<PublicKey>();

                for(int i = 0; i < (int)directory.ActiveBoosts; i++)
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

            const string refresh = "Refresh";
            const string liveView = "Live View";
            const string exit = "Exit";

            SelectionPrompt<string> displayPrompt = new SelectionPrompt<string>();
            displayPrompt.Title(_errorMessage);
            _errorMessage = String.Empty;

            displayPrompt.AddChoice(refresh);
            displayPrompt.AddChoice(liveView);
            displayPrompt.AddChoice(exit);

            string result = await displayPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

            switch (result)
            {
                case refresh:
                    return true;
                case liveView:
                    await LiveView();
                    return true;
                case exit:
                    return false;
            }

            return false;
        }

        private async Task<bool> LiveView()
        {
            Table messageTable = new Table();
            messageTable.Border(TableBorder.Square);
            messageTable.AddColumns("Time", "Updates");

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

                foreach (var stakingInfo in _stakeInfo.Where(x => x.PendingStake > 0 || x.UserStake > 0))
                {
                    boostsWatching.Add(stakingInfo.Boost.Name);

                    //Stake Account
                    states.Add(await _streamingClient.SubscribeAccountInfoAsync(stakingInfo.StakeAccount, (state, info) =>
                    {
                        Stake stake = Stake.Deserialize(Convert.FromBase64String(info.Value.Data[0]));

                        double oldUserStake = stakingInfo.UserStake;
                        double oldPendingStake = stakingInfo.PendingStake;
                        double oldRewards = stakingInfo.Rewards;

                        stakingInfo.UserStake = stake.Balance / Math.Pow(10, stakingInfo.Boost.Decimal);
                        stakingInfo.PendingStake = stake.BalancePending / Math.Pow(10, stakingInfo.Boost.Decimal);
                        stakingInfo.Rewards = stake.Rewards / OreProgram.OreDecimals;

                        if(stakingInfo.Rewards > oldRewards)
                        {
                            AddMessage($"[[{stakingInfo.Boost.Name}]] Received [green]{stakingInfo.Rewards - oldRewards:0.00000000000}[/] ORE for the hour");
                        }
                    }));

                    //Checkpoint account
                    states.Add(await _streamingClient.SubscribeAccountInfoAsync(stakingInfo.Boost.CheckpointAddress, (state, info) =>
                    {
                        CheckPoint checkpoint = CheckPoint.Deserialize(Convert.FromBase64String(info.Value.Data[0]));

                        stakingInfo.LastPayout = checkpoint.LastCheckPoint.UtcDateTime;

                    }));

                    //Proof account for pending
                    states.Add(await _streamingClient.SubscribeAccountInfoAsync(stakingInfo.Boost.BoostProof, (state, info) =>
                    {
                        Proof proof = Proof.Deserialize(Convert.FromBase64String(info.Value.Data[0]));

                        double oldUserPendingRewards = stakingInfo.UserPendingRewards;

                        stakingInfo.PendingRewards = proof.Balance / OreProgram.OreDecimals;

                        if(oldUserPendingRewards < stakingInfo.UserPendingRewards)
                        {
                            AddMessage($"[[{stakingInfo.Boost.Name}]] Received [yellow]{stakingInfo.UserPendingRewards - oldUserPendingRewards:0.00000000000}[/] ORE as pending rewards");
                        }
                    }));

                    //Boost account
                    states.Add(await _streamingClient.SubscribeAccountInfoAsync(stakingInfo.Boost.BoostAddress, (state, info) =>
                    {
                        Boost boost = Boost.Deserialize(Convert.FromBase64String(info.Value.Data[0]));

                        double oldMultiplier = boost.Multiplier;
                        ulong oldLocked = boost.Locked;

                        stakingInfo.TotalBoostStake = boost.TotalDeposits / Math.Pow(10, stakingInfo.Boost.Decimal);
                        stakingInfo.TotalStakers = boost.TotalStakers;
                        stakingInfo.Multiplier = boost.Multiplier / 1000.0;
                        stakingInfo.Locked = boost.Locked > 0;

                        if(oldLocked != boost.Locked)
                        {
                            AddMessage($"[[{stakingInfo.Boost.Name}]] Boost has been {(boost.Locked > 0 ? $"[red]locked[/] to initiate payouts" : $"[green]unlocked[/] to receive rewards")}");
                        }

                        if(oldMultiplier != stakingInfo.Multiplier)
                        {
                            AddMessage($"[[{stakingInfo.Boost.Name}]] Multiplier has been changed to [cyan]{stakingInfo.Multiplier:0.00}x[/]");
                        }

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

                AddMessage($"[green]Live updates for boosts you have staked in has started ({String.Join(", ", boostsWatching)})[/]");

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
                _stakingTable.Title("Staking Information");
                _stakingTable.AddColumns("LP", "Mult", "Stakers", "Total Stake", "Relative Yield", "User Stake", "Pending Stake", "Share", "Rewards", "Pending Rewards", "Next Checkpoint");
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
                                        $"{stakeInfo.PendingStake:0.###} (${stakeInfo.UserPendingStakeUSDValue:n2})",
                                        $"{stakeInfo.SharePercent:0.####}%",
                                        WrapBooleanColor($"{stakeInfo.Rewards:n11} (${stakeInfo.RewardUSDValue:n2})", stakeInfo.Rewards > 0, Color.Green, null),
                                        WrapBooleanColor($"{stakeInfo.UserPendingRewards:n11} (${stakeInfo.PendingUserRewardUSDValue:n2})", stakeInfo.UserPendingRewards > 0, Color.Yellow, null),
                                        WrapBooleanColor(PrettyFormatTime(nextPayoutTime), stakeInfo.Locked, Color.Red, null)
                                        );
                }
            }
            else
            {
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
                    _stakingTable.UpdateCell(i, 6, $"{stakeInfo.PendingStake:0.###} (${stakeInfo.UserPendingStakeUSDValue:n2})");
                    _stakingTable.UpdateCell(i, 7, $"{stakeInfo.SharePercent:0.####}%");
                    _stakingTable.UpdateCell(i, 8, WrapBooleanColor($"{stakeInfo.Rewards:n11} (${stakeInfo.RewardUSDValue:n2})", stakeInfo.Rewards > 0, Color.Green, null));
                    _stakingTable.UpdateCell(i, 9, WrapBooleanColor($"{stakeInfo.UserPendingRewards:n11} (${stakeInfo.PendingUserRewardUSDValue:n2})", stakeInfo.UserPendingRewards > 0, Color.Yellow, null));
                    _stakingTable.UpdateCell(i, 10, WrapBooleanColor(PrettyFormatTime(nextPayoutTime), stakeInfo.Locked, Color.Red, null));
                }
            }

            string PrettyFormatTime(TimeSpan span)
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

            int accountCount = 0;
            int i = 0;

            while(accountCount < accounts.Count)
            {
                var stakeInfo = _stakeInfo[i++];

                int totalAccountsToRead = baseAccountSize + (stakeInfo.Boost.ExtraData == null ? 0 : BoostInformation.MeteoraExtraData.TotalAccounts);

                var tAccounts = result.Result.Value.Skip(accountCount).Take(totalAccountsToRead).ToList();

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
                    stakeInfo.Locked = boost.Locked > 0;

                    if(poolType == BoostInformation.PoolType.Ore)
                    {
                        stakeInfo.TotalOreStaked = (decimal)stakeInfo.TotalBoostStake;
                        stakeInfo.TotalLPStake = (decimal)stakeInfo.TotalBoostStake;
                    }
                }

                if(proofData != null)
                {
                    Proof proof = Proof.Deserialize(Convert.FromBase64String(proofData.Data[0]));

                    stakeInfo.PendingRewards = proof.Balance / OreProgram.OreDecimals;
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
                    stakeInfo.PendingStake = stake.BalancePending / Math.Pow(10, stakeInfo.Boost.Decimal);
                    stakeInfo.Rewards = stake.Rewards / OreProgram.OreDecimals;
                }
                else
                {
                    stakeInfo.UserStake = 0;
                    stakeInfo.PendingStake = 0;
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

        private async Task<RequestResult<T>> SendWithRetry<T>(Func<Task<RequestResult<T>>> request, int retryCount = 5)
        {
            for(int i =0; i < retryCount; i++)
            {
                RequestResult<T> result = await request();

                if(result.WasSuccessful)
                {
                    return result;
                }

                await Task.Delay(1000);
            }

            return default;
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
