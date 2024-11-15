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
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Reflection.PortableExecutable;
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
        public override bool RequiresKeypair { get; } = true;
        public override Uri WebsocketUrl => new Uri($"wss://{HostName}/v2/ws?timestamp={_timestamp}");
        public virtual double MiniumumRewardPayout => 0;


        protected HttpClient _client;
        protected MinerPoolInformation _minerInformation;
        protected HQPoolSettings _poolSettings;
        protected ulong _timestamp = 0;

        protected int _currentBestDifficulty = 0;
        protected string _errorMessage = String.Empty;

        private bool _sendingReadyUp = false;

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
                    //Continue to attempt to send readyup message until successful
                    if(!_sendingReadyUp)
                    {
                        _sendingReadyUp = true;

                        while(!await SendReadyUp())
                        {
                            await Task.Delay(1000);
                        }

                        _sendingReadyUp = false;
                    }
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
                            OreHQChallengeResponse challenge = new OreHQChallengeResponse();
                            challenge.Deserialize(buffer);

                            HandleNewChallenge(challenge);
                        }
                        break;
                    case OreHQResponseTypes.SubmissionResult:
                        {
                            OreHQPoolSubmissionResponse submissionResponse = new OreHQPoolSubmissionResponse();
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

            if (!String.IsNullOrEmpty(HostName))
            {
                _client ??= new HttpClient
                {
                    BaseAddress = new Uri($"https://{WebsocketUrl.Host}")
                };
            }

            base.SetWalletInfo(wallet, publicKey);
        }

        public override async Task<(bool, string)> SetupAsync(CancellationToken token, bool initialSetup = false)
        {
            bool isComplete = true;
            string errorMessage = String.Empty;

            await AnsiConsole.Status().StartAsync($"Setting up {PoolName} pool", async ctx =>
            {
                if (_wallet == null)
                {
                    errorMessage = "A full keypair is required to sign message for this pool. Private keys are never sent to the server";

                    isComplete = false;
                }
                else
                {
                    try
                    {
                        if (!await RegisterAsync(token))
                        {
                            errorMessage = $"Failed to signup to pool";

                            isComplete = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        errorMessage = $"Failed to complete setup. Reason: {ex.Message}";
                        isComplete = false;
                    }
                }
            });

            return (isComplete, errorMessage);
        }

        public override async Task<(bool, string)> OptionsAsync(CancellationToken token)
        {
            await _poolSettings.LoadAsync();

            if(WebsocketUrl == null)
            {
                return (false, $"Domain is empty");
            }

            (bool success, string errorMessage) balanceRefresh = await RefreshStakeBalancesAsync(true, token);

            if(!balanceRefresh.success)
            {
                return (false, !String.IsNullOrEmpty(balanceRefresh.errorMessage) ? $"Failed to pull balance information. Error: {balanceRefresh.errorMessage}" : String.Empty);
            }

            if(token.IsCancellationRequested)
            {
                return (false, String.Empty);
            }

            await DisplayOptionsAsync(token);

            return (true, String.Empty);
        }

        public override string[] TableHeaders()
        {
            return ["Time", "Id", "Diff", "Mining Rewards", /*"Staking Rewards",*/ $"Pool Rewards", "Unclaimed Rewards", "Unclaimed Stake"];
        }

        #endregion

        #region Display Options

        protected async Task DisplayOptionsAsync(CancellationToken token)
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
                builder.AppendLine($"{"Mining Rewards".PadRight(14)} {_minerInformation.TotalMiningRewards} {Coins} " +
                    $"{(_minerInformation.TotalMiningRewards.BalanceChangeSinceUpdate > 0 ? $"([green]+{_minerInformation.TotalMiningRewards.BalanceChangeSinceUpdate:0.00000000000}[/])" : String.Empty)}" +
                    $"{(_minerInformation.TotalMiningRewards.TotalChange > 0 ? $"[[[Cyan]+{_minerInformation.TotalMiningRewards.TotalChange:0.00000000000} since start[/]]]" : String.Empty)}");
               
                builder.AppendLine($"{"Stake Rewards".PadRight(14)} {_minerInformation.TotalStakeRewards} {Coins} " +
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
                    builder.AppendLine($"\n{_errorMessage}");

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
                    case claimRewardBalance:
                        await ClaimRewardsOptionAsync(token);
                        break;
                    default:
                        return;
                }

                AnsiConsole.Clear();
            }

        }

        protected async Task SetClaimWalletOptionAsync(CancellationToken token)
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

        protected async Task ClaimStakeOptionAsync(CancellationToken token)
        {
            string message = String.Empty;

            while (true)
            {
                AnsiConsole.Clear();

                SelectionPrompt<OreHQPoolStake> selectionPrompt = new SelectionPrompt<OreHQPoolStake>();

                selectionPrompt.Title($"Choose staking pool to claim from {(!String.IsNullOrEmpty(message) ? $"\n\n{message}" : String.Empty)}");
                selectionPrompt.UseConverter((pool) =>
                {
                    return pool == null ? "Exit" : $"{pool.PoolName}: {pool.RewardsUI} {Coins}";
                });

                selectionPrompt.AddChoices(_minerInformation.Stakes.Where(x => x.RewardsUI > MiniumumRewardPayout));
                selectionPrompt.AddChoice(null);
                message = String.Empty;

                OreHQPoolStake result = await selectionPrompt.ShowAsync(AnsiConsole.Console, token);

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

                if(claimAmount == 0)
                {
                    return;
                }

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

        protected async Task ClaimRewardsOptionAsync(CancellationToken token)
        {
            string message = String.Empty;

            await RefreshStakeBalancesAsync(true, token);

            AnsiConsole.Clear();

            //Display claim prompt
            TextPrompt<double> claimPrompt = new TextPrompt<double>($"Enter claim amount between ({MiniumumRewardPayout}-{_minerInformation.TotalMiningRewards.CurrentBalance}): ");
            claimPrompt.DefaultValue(_minerInformation.TotalMiningRewards.CurrentBalance);
            claimPrompt.Validate((v) =>
            {
                return v == 0 || (v >= MiniumumRewardPayout && v <= _minerInformation.TotalMiningRewards.CurrentBalance);
            });

            double claimAmount = await claimPrompt.ShowAsync(AnsiConsole.Console, token);

            if (claimAmount == 0)
            {
                return;
            }

            //Confirmation
            string claimWallet = _poolSettings.ClaimWallet ?? _wallet.Account.PublicKey;
            bool isSame = claimWallet == _wallet.Account.PublicKey;

            ConfirmationPrompt confirmationPrompt = new ConfirmationPrompt($"Claim {claimAmount} {Coins} mining rewards to {claimWallet}" +
                $"{(!isSame ? $"\n[yellow]Warning: Claim wallet {claimWallet} is different from mining wallet {_wallet.Account.PublicKey}. Continue?[/]" : String.Empty)}?");
            confirmationPrompt.DefaultValue = false;

            if (await confirmationPrompt.ShowAsync(AnsiConsole.Console, token))
            {
                ulong ulongClaimAmount = (ulong)(claimAmount * (Coins == Coin.Ore ? OreProgram.OreDecimals : CoalProgram.CoalDecimals));

                //Try claim
                (bool success, string message) claimResult = await ClaimMiningRewards(claimWallet, ulongClaimAmount, token);

                if (claimResult.success)
                {
                    _errorMessage = $"[green]{claimResult.message}[/]";
                }
                else
                {
                    _errorMessage = $"[red]Failed to claim: {claimResult.message}[/]";
                }
            }
            else
            {
                _errorMessage = $"[yellow]Notice: User canceled claiming[/]";
            }
        }

        #endregion

        #region HTTP Requests

        protected virtual async Task<bool> RegisterAsync(CancellationToken token)
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

        protected virtual async Task<bool> UpdateTimestampAsync(CancellationToken token)
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
                _logger.Log(LogLevel.Warn, $"Failed to request timestamp from {WebsocketUrl.Host}. Reason: {ex.Message}");

                return false;
            }
        }

        protected virtual async Task<(bool success, string errorMessage)> RefreshStakeBalancesAsync(bool displayUI, CancellationToken token)
        {
            try
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

                return (true, String.Empty);
            }
            catch(TaskCanceledException ex)
            {
                return (false, String.Empty);
            }
            catch(Exception ex)
            {
                return (false, ex.Message);
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

        protected virtual async Task<(double value, bool success)> GetF64DataAsync(string endpoint, CancellationToken token)
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
                _logger.Log(LogLevel.Warn, $"Failed to request {endpoint} data from pool. Reason: {ex.Message}");

                return default;
            }
        }

        protected virtual async Task<(double balance, bool success)> GetBalanceAsync(CancellationToken token)
        {
            return await GetF64DataAsync("balance", token);
        }

        protected virtual async Task<(double reward, bool success)> GetRewardAsync(CancellationToken token)
        {
            return await GetF64DataAsync("rewards", token);
        }

        protected virtual async Task<List<OreHQPoolStake>> GetStakingInformationAsync(CancellationToken token)
        {
            try
            {
                using var response = await _client.GetAsync($"/v2/miner/boost/stake-accounts?pubkey={_publicKey}", token);

                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    return default;
                }

                string data = await response.Content.ReadAsStringAsync();

                try
                {
                    return JsonConvert.DeserializeObject<List<OreHQPoolStake>>(data);

                }
                catch(JsonReaderException) //Typically invalid pubkey
                {
                    return null;
                }
            }
            catch
            {
                _logger.Log(LogLevel.Warn, $"Failed to grab staking information from pool");

                return null;
            }
        }

        protected virtual async Task<(bool success, string message)> ClaimStakeRewards(string claimWallet, string mintId, ulong amount, CancellationToken token)
        {
            try
            {
                using var response = await _client.PostAsync($"/v3/claim-stake-rewards?pubkey={claimWallet}&mint={mintId}&amount={amount}", null, token);
                string data = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    return (false, String.IsNullOrEmpty(data) ? $"Server returned status code {response.StatusCode}" : data);
                }

                switch (data)
                {
                    case "SUCCESS":
                        return (true, $"Successfully queued stake claim request to {claimWallet}. Balances will update after transaction is finalized");
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

        protected virtual async Task<(bool success, string message)> ClaimMiningRewards(string claimWallet, ulong amount, CancellationToken token)
        {
            try
            {
                if(!await UpdateTimestampAsync(token))
                {
                    return (false, $"Failed to get timestamp from server");
                }

                Base58Encoder _encoder = new Base58Encoder();

                byte[] tBytes = new byte[8+32+8];
                BinaryPrimitives.WriteUInt64LittleEndian(tBytes, _timestamp);
                _encoder.DecodeData(claimWallet).CopyTo(tBytes, 8);
                BinaryPrimitives.WriteUInt64LittleEndian(tBytes.AsSpan().Slice(40, 8), amount);

                byte[] sigBytes = _wallet.Sign(tBytes);
                string sig = _encoder.EncodeData(sigBytes);

                string authHeader = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_publicKey}:{sig}"))}";

                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"/v2/claim?timestamp={_timestamp}&receiver_pubkey={claimWallet}&amount={amount}");
                requestMessage.Headers.TryAddWithoutValidation("Authorization", authHeader);

                using var response = await _client.SendAsync(requestMessage);
                string data = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    return (false, String.IsNullOrEmpty(data) ? $"Server returned status code {response.StatusCode}" : data);
                }

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
                return (false, $"Unknown exception occurred while claiming. Result: {ex.Message}");
            }
        }

        #endregion

        #region WS Responses

        protected virtual void HandleNewChallenge(OreHQChallengeResponse challengeResponse)
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

        protected virtual async void HandleSubmissionResult(OreHQPoolSubmissionResponse submissionResponse)
        {
            CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            //This data takes ~20s to update properly, so everything is off by 1 update
            await RefreshStakeBalancesAsync(false, cts.Token);

            OnMinerUpdate?.Invoke(this, [
                DateTime.Now.ToShortTimeString(), 
                GenerateChallengeId(submissionResponse.Challenge).ToString(),
                $"{submissionResponse.MinerSuppliedDifficulty}/{submissionResponse.Difficulty}",
                $"{submissionResponse.MinerEarnedRewards:0.00000000000}",
                //$"{_minerInformation.TotalStakeRewards.BalanceChangeSinceUpdate:0.00000000000}",
                $"{submissionResponse.TotalRewards:0.00000000000}",
                $"{_minerInformation.TotalMiningRewards:0.00000000000}",
                $"{_minerInformation.TotalStakeRewards:0.00000000000}",
            ]);
        }

        protected virtual int GenerateChallengeId(byte[] data)
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

        protected virtual async Task<bool> SendReadyUp(bool updateTime = true)
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

        protected virtual async Task<bool> SendPoolSubmission(DifficultyInfo info)
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

        protected class MinerPoolInformation
        {
            public BalanceTracker<double> TotalStakeRewards { get; private set; } = new BalanceTracker<double>();
            public BalanceTracker<double> TotalMiningRewards { get; private set; } = new BalanceTracker<double>();
            public BalanceTracker<double> WalletBalance { get; private set; } = new BalanceTracker<double>();

            public List<OreHQPoolStake> Stakes { get; set; }

            private Coin _coin;

            public MinerPoolInformation(Coin coin)
            {
                _coin = coin;
            }

            public void UpdateStakes(List<OreHQPoolStake> stakes)
            {
                Stakes = stakes;

                double totalStakeRewards = 0;

                var stakeMints = _coin == Coin.Ore ? OreProgram.BoostMints : null;

                if(stakeMints == null)
                {
                    return;
                }

                var boostAccounts = _coin == Coin.Ore ? OreProgram.BoostMints : null;
                
                foreach(OreHQPoolStake stake in stakes)
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

        protected class HQPoolSettings : PoolSettings
        {
            public string ClaimWallet { get; set; }

            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string CustomDomain { get; set; }

            public HQPoolSettings(string poolName) : base(poolName)
            {
            }
        }

        #endregion
    }
}
