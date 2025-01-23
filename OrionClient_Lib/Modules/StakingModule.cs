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
using Solnet.Rpc;
using Solnet.Rpc.Core.Http;
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
            _streamingClient = ClientFactory.GetStreamingClient(data.Settings.RPCSetting.Url);
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
            catch(TaskCanceledException)
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
        }

        private async Task<bool> DisplayOptions()
        {
            await UpdateStakeInformation();
            UpdateStakingTable();

            AnsiConsole.Write(_stakingTable);

            const string refresh = "Refresh";
            const string exit = "Exit";

            SelectionPrompt<string> test = new SelectionPrompt<string>();
            test.Title("");
            test.AddChoice("Refresh");
            test.AddChoice("Exit");

            string result = await test.ShowAsync(AnsiConsole.Console, _cts.Token);

            switch (result)
            {
                case refresh:
                    return true;
                case exit:
                    return false;
            }

            return false;
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
                    var relativeProfit = stakeInfo.Multiplier == 0 || stakeInfo.LPTotalUSDValue  == 0? 0 : (double)oreBoost.LPTotalUSDValue / ((double)stakeInfo.LPTotalUSDValue / stakeInfo.Multiplier);


                    _stakingTable.AddRow(stakeInfo.Boost.Name,
                                        $"{stakeInfo.Multiplier:0.##}x",
                                        stakeInfo.TotalStakers.ToString(),
                                        $"{stakeInfo.TotalStake:0.###} (${stakeInfo.LPTotalUSDValue:n2})",
                                        $"{relativeProfit:0.00}x",
                                        $"{stakeInfo.UserStake:0.###} (${stakeInfo.UserStakeUSDValue:n2})", 
                                        $"{stakeInfo.PendingStake:0.###} (${stakeInfo.UserPendingStakeUSDValue:n2})",
                                        $"{stakeInfo.SharePercent:0.####}%",
                                        $"[yellow]{stakeInfo.Rewards:n11} (${stakeInfo.RewardUSDValue:n2})[/]",
                                        $"[yellow]{stakeInfo.UserPendingRewards:n11} (${stakeInfo.PendingUserRewardUSDValue:n2})[/]",
                                        PrettyFormatTime(nextPayoutTime)
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
                    var relativeProfit = (double)oreBoost.LPTotalUSDValue / ((double)stakeInfo.LPTotalUSDValue / stakeInfo.Multiplier);


                    _stakingTable.UpdateCell(i, 1, $"{stakeInfo.Multiplier:0.##}x");
                    _stakingTable.UpdateCell(i, 2, stakeInfo.TotalStakers.ToString());
                    _stakingTable.UpdateCell(i, 3, $"{stakeInfo.TotalStake:0.###} (${stakeInfo.LPTotalUSDValue:n2})");
                    _stakingTable.UpdateCell(i, 4, $"{relativeProfit:0.00}x");
                    _stakingTable.UpdateCell(i, 5, $"{stakeInfo.UserStake:0.###} (${stakeInfo.UserStakeUSDValue:n2})");
                    _stakingTable.UpdateCell(i, 6, $"{stakeInfo.PendingStake:0.###} (${stakeInfo.UserPendingStakeUSDValue:n2})");
                    _stakingTable.UpdateCell(i, 7, $"{stakeInfo.SharePercent:0.####}%");
                    _stakingTable.UpdateCell(i, 8, $"[yellow]{stakeInfo.Rewards:n11} (${stakeInfo.RewardUSDValue:n2})[/]");
                    _stakingTable.UpdateCell(i, 9, $"[yellow]{stakeInfo.UserPendingRewards:n11} (${stakeInfo.PendingUserRewardUSDValue:n2})[/]");
                    _stakingTable.UpdateCell(i, 10, PrettyFormatTime(nextPayoutTime));
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

                if (await UpdateMeteroaPool(_stakeInfo.Where(x => x.Boost.Type == BoostInformation.PoolType.Meteroa)))
                {
                    AnsiConsole.MarkupLine($"[green]Successfully updated meteroa pool info[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]Failed to update meteroa pool info[/]");
                }

                //Set native ore price
                StakeInformation? nativeOre = _stakeInfo.FirstOrDefault(x => x.Boost.Type == BoostInformation.PoolType.Ore);
                nativeOre.LPUSDValue = _stakeInfo.OrderByDescending(x => x.LastUpdated).FirstOrDefault(x => x.Boost.Type != BoostInformation.PoolType.Ore)?.OreUSDValue ?? 0;
                nativeOre.OreUSDValue = nativeOre.LPUSDValue;
                nativeOre.LastUpdated = DateTime.UtcNow;
            });

            AnsiConsole.Clear();
        }

        private async Task<bool> UpdateBoostInformation()
        {
            List<string> accounts = new List<string>();

            foreach(var info in _stakeInfo)
            {
                accounts.Add(info.Boost.BoostAddress);
                accounts.Add(info.StakeAccount);
                accounts.Add(info.Boost.BoostProof);
                accounts.Add(info.Boost.CheckpointAddress);
            }

            int totalAccounts = accounts.Count / _stakeInfo.Count;

            var result = await SendWithRetry(() => _client.GetMultipleAccountsAsync(accounts, Solnet.Rpc.Types.Commitment.Confirmed));

            if(result == null)
            {
                return false;
            }

            for(int i = 0; i < accounts.Count / totalAccounts; i++)
            {
                var tAccounts = result.Result.Value.Skip(i * totalAccounts).Take(totalAccounts).ToList();
                StakeInformation stakeInfo = _stakeInfo[i];

                var boostData = tAccounts[0];
                var stakeData = tAccounts[1];
                var proofData = tAccounts[2];
                var checkpointData = tAccounts[3];

                if(boostData != null)
                {
                    Boost boost = Boost.Deserialize(Convert.FromBase64String(boostData.Data[0]));

                    stakeInfo.TotalStake = boost.TotalDeposits / Math.Pow(10, stakeInfo.Boost.Decimal);
                    stakeInfo.TotalStakers = boost.TotalStakers;
                    stakeInfo.Multiplier = boost.Multiplier / 1000.0;

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
                    boost.LPUSDValue = boostData.SharePrice;
                    boost.OreUSDValue = boostData.TokenAMint == OreProgram.MintId.Key ? boostData.VaultBalances_.TokenA.TokenUSDValue : boostData.VaultBalances_.TokenB.TokenUSDValue;
                    boost.TokenBUSDValue = boostData.TokenAMint != OreProgram.MintId.Key ? boostData.VaultBalances_.TokenA.TokenUSDValue : boostData.VaultBalances_.TokenB.TokenUSDValue;
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
                        boost.LPUSDValue = poolInfo.PoolLpPriceInUsd;
                        boost.OreUSDValue = poolInfo.PoolTokenMints[0] == OreProgram.MintId.Key ? (poolInfo.PoolTokenUsdAmounts[0] / poolInfo.PoolTokenAmounts[0]) : (poolInfo.PoolTokenUsdAmounts[1] / poolInfo.PoolTokenAmounts[1]);
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
    }
}
