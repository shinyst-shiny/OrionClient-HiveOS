using DrillX;
using DrillX.Solver;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using Org.BouncyCastle.Asn1.Cmp;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto.Signers;
using OrionClientLib.CoinPrograms;
using OrionClientLib.Hashers.Models;
using OrionClientLib.Modules.Models;
using OrionClientLib.Pools.HQPool;
using OrionClientLib.Pools.Models;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;
using Spectre.Console;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static System.Net.WebRequestMethods;

namespace OrionClientLib.Pools
{
    public enum OreHQRequestTypes { Ready, Submission };
    public enum OreHQResponseTypes { StartMining, SubmissionResult };

    public abstract class OreHQPool : WebsocketPool
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public override event EventHandler<NewChallengeInfo> OnChallengeUpdate;
        public override event EventHandler<string[]> OnMinerUpdate;
        public override event EventHandler PauseMining;
        public override event EventHandler ResumeMining;

        public abstract override string PoolName { get; }
        public abstract override string DisplayName { get; }
        public abstract override string Description { get; }
        public abstract override Dictionary<string, string> Features { get; }
        public abstract override Coin Coins { get; }

        public abstract override bool HideOnPoolList { get; }
        public override Uri WebsocketUrl => new Uri($"wss://{HostName}/v2/ws?timestamp={_timestamp}");
        public virtual double MiniumumRewardPayout => 0;


        private HttpClient _client;
        private MinerPoolInformation _minerInformation;
        private HQPoolSettings _poolSettings;
        private ulong _timestamp = 0;

        private int _currentBestDifficulty = 0;
        private string _errorMessage = String.Empty;

        #region Overrides

        public override async void DifficultyFound(DifficultyInfo info)
        {
            if(info.BestDifficulty <= _currentBestDifficulty)
            {
                return;
            }

            _currentBestDifficulty = info.BestDifficulty;

            if (_currentBestDifficulty > 9)
            {
                await SendPoolSubmission(info);
            }
        }

        public override async Task<double> GetFeeAsync(CancellationToken token)
        {
            return 5;
        }

        public override async void OnMessage(ArraySegment<byte> buffer, WebSocketMessageType type)
        {
            if (type == WebSocketMessageType.Text)
            {
                string message = Encoding.UTF8.GetString(buffer);

                const string serverMineSend = "Server is sending mine transaction...";

                if (message == serverMineSend)
                {
                    await SendReadyUp();
                }
                else
                {
                    _logger.Log(LogLevel.Debug, $"Server sent message: {message}");
                }
            }
            else
            {
                switch ((OreHQResponseTypes)buffer[0])
                {
                    case OreHQResponseTypes.StartMining:
                        {
                            ChallengeResponse challenge = new ChallengeResponse();
                            challenge.Deserialize(buffer);

                            HandleNewChallenge(challenge);
                        }
                        break;
                    case OreHQResponseTypes.SubmissionResult:
                        {
                            PoolSubmissionResponse submissionResponse = new PoolSubmissionResponse();
                            submissionResponse.Deserialize(buffer);

                            HandleSubmissionResult(submissionResponse);
                        }
                        break;
                    default:
                        _logger.Log(LogLevel.Warn, $"Unknown message type {buffer[0]}");
                        break;
                }
            }
        }

        public override async Task<bool> ConnectAsync(CancellationToken token)
        {
            if(!await UpdateTimestampAsync(token))
            {
                return false;
            }

            byte[] tBytes = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(tBytes, _timestamp);

            Base58Encoder _encoder = new Base58Encoder();
            byte[] sigBytes = _wallet.Sign(tBytes);
            string sig = _encoder.EncodeData(sigBytes);

            _authorization = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_publicKey}:{sig}"))}";
            bool result = await base.ConnectAsync(token);

            await RefreshStakeBalancesAsync(false, token);

            return result && await SendReadyUp(false);
        }

        public override void SetWalletInfo(Wallet wallet, string publicKey)
        {
            _minerInformation ??= new MinerPoolInformation(Coins);
            _poolSettings ??= new HQPoolSettings(PoolName);
            _client ??= new HttpClient
            {
                BaseAddress = new Uri($"https://{WebsocketUrl.Host}")
            };

            base.SetWalletInfo(wallet, publicKey);
        }

        public override async Task<bool> SetupAsync(CancellationToken token, bool initialSetup = false)
        {
            bool isComplete = true;

            await AnsiConsole.Status().StartAsync($"Setting up {PoolName} pool", async ctx =>
            {
                if (_wallet == null)
                {
                    AnsiConsole.MarkupLine("[red]A full keypair is required to sign message for this pool. Private keys are never sent to the server[/]\n");

                    isComplete = false;
                }
                else
                {
                    try
                    {
                        if (!await RegisterAsync(token))
                        {
                            AnsiConsole.MarkupLine($"[red]Failed to signup to pool[/]\n");

                            isComplete = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Failed to complete setup. Reason: {ex.Message}[/]\n");
                    }
                }
            });

            return isComplete;
        }

        public override async Task OptionsAsync(CancellationToken token)
        {
            await _poolSettings.LoadAsync();

            await RefreshStakeBalancesAsync(true, token);

            if(token.IsCancellationRequested)
            {
                return;
            }

            await DisplayOptionsAsync(token);
        }

        public override string[] TableHeaders()
        {
            return ["Time", "Id", "Diff", "Mining Rewards", "Staking Rewards", $"Pool Rewards", "Unclaimed Rewards", "Unclaimed Stake"];
        }

        #endregion

        #region Display Options

        private async Task DisplayOptionsAsync(CancellationToken token)
        {
            while (true)
            {
                StringBuilder builder = new StringBuilder();
                builder.AppendLine($"Public key: {_publicKey}");

                if(!String.IsNullOrEmpty(_poolSettings.ClaimWallet))
                {
                    builder.AppendLine($"Claim wallet: {_poolSettings.ClaimWallet ?? _publicKey}");
                }

                builder.AppendLine();

                builder.AppendLine($"{"Wallet Balance".PadRight(14)} {_minerInformation.WalletBalance} {Coins}");
                builder.AppendLine($"{"Unclaimed Mining Rewards".PadRight(14)} {_minerInformation.TotalMiningRewards} {Coins} " +
                    $"{(_minerInformation.TotalMiningRewards.BalanceChangeSinceUpdate > 0 ? $"([green]+{_minerInformation.TotalMiningRewards.BalanceChangeSinceUpdate:0.00000000000}[/])" : String.Empty)}" +
                    $"{(_minerInformation.TotalMiningRewards.TotalChange > 0 ? $"[[[Cyan]+{_minerInformation.TotalMiningRewards.TotalChange:0.00000000000} since start[/]]]" : String.Empty)}");
               
                builder.AppendLine($"{"Unclaimed Stake Rewards".PadRight(14)} {_minerInformation.TotalStakeRewards} {Coins} " +
                    $"{(_minerInformation.TotalStakeRewards.BalanceChangeSinceUpdate > 0 ? $"([green]+{_minerInformation.TotalStakeRewards.BalanceChangeSinceUpdate:0.00000000000}[/])" : String.Empty)}" +
                    $"{(_minerInformation.TotalStakeRewards.TotalChange > 0 ? $"[[[Cyan]+{_minerInformation.TotalStakeRewards.TotalChange:0.00000000000} since start[/]]]" : String.Empty)}");

                if (_minerInformation.Stakes?.Count > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine($"Stake Info:");

                    if (_minerInformation.Stakes != null)
                    {
                        foreach (var stakeInfo in _minerInformation.Stakes)
                        {
                            OreProgram.BoostMints.TryGetValue(new PublicKey(stakeInfo.MintPubkey), out (string name, int decimals) data);

                            builder.AppendLine($"   {(data.name ?? "??").PadRight(11)} {stakeInfo.StakedBalance / Math.Pow(10, !String.IsNullOrEmpty(data.name) ? data.decimals : 11)}");
                        }
                    }
                }

                if (!String.IsNullOrEmpty(_errorMessage))
                {
                    builder.AppendLine($"\n[red]Error: {_errorMessage}[/]");

                    _errorMessage = String.Empty;
                }

                SelectionPrompt<string> choices = new SelectionPrompt<string>();
                choices.Title(builder.ToString());

                const string claimRewardBalance = "Claim Mining Rewards";
                const string claimStakeBalance = "Claim Staking Rewards";
                const string changeWallet = "Change Claim Wallet";
                const string refreshBalance = "Refresh Balance";

                if (_minerInformation.TotalMiningRewards.CurrentBalance > MiniumumRewardPayout)
                {
                    choices.AddChoice(claimRewardBalance);
                }

                if (_minerInformation.Stakes?.Any(x => x.RewardsUI > MiniumumRewardPayout) == true)
                {
                    choices.AddChoice(claimStakeBalance);
                }

                choices.AddChoice(refreshBalance);
                choices.AddChoice(changeWallet);
                choices.AddChoice("Exit");

                string choice = await choices.ShowAsync(AnsiConsole.Console, token);

                switch (choice)
                {
                    case refreshBalance:
                        await RefreshStakeBalancesAsync(true, token);
                        break;
                    case changeWallet:
                        await SetClaimWalletOptionAsync(token);
                        break;
                    case claimStakeBalance:
                        await ClaimStakeOptionAsync(token);
                        break;
                    default:
                        return;
                }

                AnsiConsole.Clear();
            }

        }

        private async Task SetClaimWalletOptionAsync(CancellationToken token)
        {
            Base58Encoder encoder = new Base58Encoder();

            TextPrompt<string> textPrompt = new TextPrompt<string>("Set wallet to claim rewards to. Type [yellow]clear[/] to remove. Default: ");
            textPrompt.DefaultValue(_poolSettings.ClaimWallet);
            textPrompt.Validate((str) =>
            {
                if (str.ToLower() == "clear")
                {
                    return true;
                }

                try
                {
                    if (encoder.DecodeData(str).Length != 32)
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }

                return true;
            });

            string result = await textPrompt.ShowAsync(AnsiConsole.Console, token);

            if (result?.ToLower() == "clear")
            {
                _poolSettings.ClaimWallet = null;
            }
            else
            {
                _poolSettings.ClaimWallet = result;
            }

            await _poolSettings.SaveAsync();
        }

        private async Task ClaimStakeOptionAsync(CancellationToken token)
        {
            string message = String.Empty;

            while (true)
            {
                AnsiConsole.Clear();

                SelectionPrompt<PoolStake> selectionPrompt = new SelectionPrompt<PoolStake>();

                selectionPrompt.Title($"Choose staking pool to claim from {(!String.IsNullOrEmpty(message) ? $"\n\n{message}" : String.Empty)}");
                selectionPrompt.UseConverter((pool) =>
                {
                    return pool == null ? "Exit" : $"{pool.PoolName}: {pool.RewardsUI} {Coins}";
                });

                selectionPrompt.AddChoices(_minerInformation.Stakes.Where(x => x.RewardsUI > MiniumumRewardPayout));
                selectionPrompt.AddChoice(null);
                message = String.Empty;

                PoolStake result = await selectionPrompt.ShowAsync(AnsiConsole.Console, token);

                //Exit
                if (result == null)
                {
                    return;
                }

                //Display claim prompt
                TextPrompt<double> claimPrompt = new TextPrompt<double>($"Enter claim amount between ({MiniumumRewardPayout}-{result.RewardsUI}): ");
                claimPrompt.DefaultValue(result.RewardsUI);
                claimPrompt.Validate((v) =>
                {
                    return v == 0 || (v >= MiniumumRewardPayout && v <= result.RewardsUI);
                });

                double claimAmount = await claimPrompt.ShowAsync(AnsiConsole.Console, token);

                //Confirmation
                string claimWallet = _poolSettings.ClaimWallet ?? _wallet.Account.PublicKey;
                bool isSame = claimWallet == _wallet.Account.PublicKey;

                ConfirmationPrompt confirmationPrompt = new ConfirmationPrompt($"Claim {claimAmount} {Coins} from {result.PoolName} pool to {claimWallet}" +
                    $"{(!isSame ? $"\n[yellow]Warning: Claim wallet {claimWallet} is different from mining wallet {_wallet.Account.PublicKey}. Continue?[/]" : String.Empty)}?");
                confirmationPrompt.DefaultValue = false;

                if(await confirmationPrompt.ShowAsync(AnsiConsole.Console, token))
                {
                    //Try claim
                    (bool success, string message) claimResult = await ClaimStakeRewards(claimWallet, result.MintPubkey, (ulong)(claimAmount * result.Decimals), token);

                    if(claimResult.success)
                    {
                        message = $"[green]{claimResult.message}[/]";
                        result.RewardsBalance -= (long)(claimAmount * result.Decimals);
                    }
                    else
                    {
                        message = $"[red]{claimResult.message}[/]";
                    }
                }
                else
                {
                    message = $"[yellow]Notice: User canceled claiming[/]";
                }

            }
        }

        #endregion

        #region HTTP Requests

        private async Task<bool> RegisterAsync(CancellationToken token)
        {
            using var response = await _client.PostAsync($"/v2/signup?miner={_publicKey}", null, token);

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                return false;
            }

            string data = await response.Content.ReadAsStringAsync();

            if(data == "SUCCESS" || data == "EXISTS")
            {
                return true;
            }

            return false;
        }

        protected async Task<bool> UpdateTimestampAsync(CancellationToken token)
        {
            try
            {
                using var response = await _client.GetAsync($"/timestamp", token);

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    return false;
                }

                string data = await response.Content.ReadAsStringAsync();

                if (!ulong.TryParse(data, out ulong value))
                {
                    return false;
                }

                _timestamp = value;

                return true;
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Error, $"Failed to request timestamp from {WebsocketUrl.Host}. Result: {ex.Message}");

                return false;
            }
        }

        private async Task RefreshStakeBalancesAsync(bool displayUI, CancellationToken token)
        {
            if (displayUI)
            {
                await AnsiConsole.Status().StartAsync($"Grabbing balance information", async ctx =>
                {
                    await Update();
                });
            }
            else
            {
                await Update();
            }

            async Task Update()
            {
                var balanceInfo = await GetBalanceAsync(token);

                if (balanceInfo.success)
                {
                    _minerInformation.UpdateWalletBalance(balanceInfo.balance);

                }

                var rewardInfo = await GetRewardAsync(token);

                if (rewardInfo.success)
                {
                    _minerInformation.UpdateMiningRewards(rewardInfo.reward);

                }

                _minerInformation.UpdateStakes(await GetStakingInformationAsync(token));
            }
        }

        private async Task<(double value, bool success)> GetF64DataAsync(string endpoint, CancellationToken token)
        {
            try
            {
                using var response = await _client.GetAsync($"/miner/{endpoint}?pubkey={_publicKey}", token);

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    return default;
                }

                string data = await response.Content.ReadAsStringAsync();

                if (!double.TryParse(data, out double value))
                {
                    return default;
                }

                return (value, true);
            }
            catch(Exception ex)
            {
                _logger.Log(LogLevel.Error, $"Failed to request {endpoint} data. Result: {ex.Message}");

                return default;
            }
        }

        private async Task<(double balance, bool success)> GetBalanceAsync(CancellationToken token)
        {
            return await GetF64DataAsync("balance", token);
        }

        private async Task<(double reward, bool success)> GetRewardAsync(CancellationToken token)
        {
            return await GetF64DataAsync("rewards", token);
        }

        private async Task<List<PoolStake>> GetStakingInformationAsync(CancellationToken token)
        {
            using var response = await _client.GetAsync($"/v2/miner/boost/stake-accounts?pubkey={_publicKey}", token);

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                return default;
            }

            string data = await response.Content.ReadAsStringAsync();

            try
            {
                return JsonConvert.DeserializeObject<List<PoolStake>>(data);

            }
            catch
            {
                return null;
            }
        }

        private async Task<(bool success, string message)> ClaimStakeRewards(string claimWallet, string mintId, ulong amount, CancellationToken token)
        {
            try
            {
                using var response = await _client.PostAsync($"/v3/claim-stake-rewards?pubkey={claimWallet}&mint={mintId}&amount={amount}", null, token);

                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"Server returned status code {response.StatusCode}");
                }

                string data = await response.Content.ReadAsStringAsync();

                switch (data)
                {
                    case "SUCCESS":
                        return (true, $"Successfully queued claim request to {claimWallet}. Balances will update after transaction is finalized");
                    case "QUEUED":
                        return (true, "Claim is already queued");
                    default:
                        if (ulong.TryParse(data, out ulong time))
                        {
                            ulong timeLeft = 1800 - time;
                            ulong secs = timeLeft % 60;
                            ulong mins = (timeLeft / 60) % 60;

                            return (false, $"You cannot claim until the time is up. Time left until next claim available: {mins}m {secs}s");
                        }

                        return (false, data);
                }
            }
            catch (Exception ex)
            {
                return (false, $"Failed to claim stake rewards. Result: {ex.Message}");
            }
        }

        #endregion

        #region WS Responses

        private void HandleNewChallenge(ChallengeResponse challengeResponse)
        {
            _currentBestDifficulty = 0;

            OnChallengeUpdate?.Invoke(this, new NewChallengeInfo
            {
                ChallengeId = GenerateChallengeId(challengeResponse.Challenge),
                Challenge = challengeResponse.Challenge,
                StartNonce = challengeResponse.StartNonce,
                EndNonce = challengeResponse.EndNonce
            });
        }

        private async void HandleSubmissionResult(PoolSubmissionResponse submissionResponse)
        {
            CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            await RefreshStakeBalancesAsync(false, cts.Token);

            OnMinerUpdate?.Invoke(this, [
                DateTime.Now.ToShortTimeString(), 
                GenerateChallengeId(submissionResponse.Challenge).ToString(),
                $"{submissionResponse.MinerSuppliedDifficulty}/{submissionResponse.Difficulty}",
                $"{submissionResponse.MinerEarnedRewards:0.00000000000}",
                $"{_minerInformation.TotalStakeRewards.BalanceChangeSinceUpdate:0.00000000000}",
                $"{submissionResponse.TotalRewards:0.00000000000}",
                $"{_minerInformation.TotalMiningRewards:0.00000000000}",
                $"{_minerInformation.TotalStakeRewards:0.00000000000}",
            ]);
        }

        private int GenerateChallengeId(byte[] data)
        {
            unchecked
            {
                const int p = 16777619;
                int hash = (int)2166136261;

                for (int i = 0; i < data.Length; i++)
                {
                    hash = (hash ^ data[i]) * p;
                }

                //Reduce to an easier number
                return Math.Abs(hash % 65536);
            }
        }

        #endregion

        #region WS Requests

        private async Task<bool> SendReadyUp(bool updateTime = true)
        {
            PauseMining?.Invoke(this, EventArgs.Empty);

            CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            if(updateTime && !await UpdateTimestampAsync(cts.Token))
            {
                return false;
            }

            byte[] tBytes = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(tBytes, _timestamp);
            byte[] sigBytes = _wallet.Sign(tBytes);

            bool result = await SendMessageAsync(new ReadyRequestMessage
            {
                PublicKey = _wallet.Account.PublicKey,
                Timestamp = _timestamp,
                Signature = sigBytes
            });

            if(result)
            {
                _logger.Log(LogLevel.Info, $"Waiting for new challenge");

                return result;
            }

            _logger.Log(LogLevel.Warn, $"Failed to send ready up message");

            return result;
        }

        private async Task<bool> SendPoolSubmission(DifficultyInfo info)
        {
            _logger.Log(LogLevel.Debug, $"Sending solution. Diff: {info.BestDifficulty}. Challenge id: {info.ChallengeId}. Nonce: {info.BestNonce}");

            byte[] nonce = new byte[24];
            info.BestSolution.CopyTo(nonce, 0);
            BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan().Slice(16, 8), info.BestNonce);

            Base58Encoder encoder = new Base58Encoder();

            bool result = await SendMessageAsync(new DiffSubmissionRequest
            {
                Digest = info.BestSolution,
                Nonce = info.BestNonce,
                PublicKey = _wallet.Account.PublicKey,
                B58Signature = encoder.EncodeData(_wallet.Account.Sign(nonce))
            });

            return result;
        }

        #endregion

        #region Classes

        private class MinerPoolInformation
        {
            public BalanceTracker<double> TotalStakeRewards { get; private set; } = new BalanceTracker<double>();
            public BalanceTracker<double> TotalMiningRewards { get; private set; } = new BalanceTracker<double>();
            public BalanceTracker<double> WalletBalance { get; private set; } = new BalanceTracker<double>();

            public List<PoolStake> Stakes { get; set; }

            private Coin _coin;

            public MinerPoolInformation(Coin coin)
            {
                _coin = coin;
            }

            public void UpdateStakes(List<PoolStake> stakes)
            {
                Stakes = stakes;

                double totalStakeRewards = 0;

                var stakeMints = _coin == Coin.Ore ? OreProgram.BoostMints : null;

                if(stakeMints == null)
                {
                    return;
                }

                var boostAccounts = _coin == Coin.Ore ? OreProgram.BoostMints : null;
                
                foreach(PoolStake stake in stakes)
                {
                    if(boostAccounts != null && boostAccounts.TryGetValue(new PublicKey(stake.MintPubkey), out var d))
                    {
                        stake.PoolName = d.name;
                        stake.Decimals = Math.Pow(10, d.decimals);
                    }

                    totalStakeRewards += stake.RewardsBalance / (_coin == Coin.Ore ? OreProgram.OreDecimals : CoalProgram.CoalDecimals);
                }

                TotalStakeRewards.Update(totalStakeRewards);
            }

            public void UpdateMiningRewards(double rewards)
            {
                TotalMiningRewards.Update(rewards);
            }

            public void UpdateWalletBalance(double balance)
            {
                WalletBalance.Update(balance);
            }
        }

        private class HQPoolSettings : PoolSettings
        {
            public string ClaimWallet { get; set; }

            public HQPoolSettings(string poolName) : base(poolName)
            {
            }
        }

        #endregion
    }
}
