using DrillX;
using DrillX.Solver;
using Newtonsoft.Json;
using NLog;
using Org.BouncyCastle.Asn1.Cmp;
using Org.BouncyCastle.Bcpg.OpenPgp;
using OrionClientLib.CoinPrograms.Ore;
using OrionClientLib.Hashers.Models;
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

        public abstract override bool HideOnPoolList { get; }
        public override Uri WebsocketUrl => new Uri($"wss://{HostName}/v2/ws?timestamp={_timestamp}");

        private HttpClient _client;
        private MinerPoolInformation _minerInformation = new MinerPoolInformation();
        private int _challengeId = 0;
        private HQPoolSettings _poolSettings;
        private ulong _timestamp = 0;
        private System.Timers.Timer _cutOffTimer;
        private byte[] _challenge = null;

        private int _currentBestDifficulty = 0;

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

            return result && await SendReadyUp(false);
        }

        public override void SetWalletInfo(Wallet wallet, string publicKey)
        {
            _poolSettings ??= new HQPoolSettings(PoolName);
            _client ??= new HttpClient
            {
                BaseAddress = new Uri($"https://{WebsocketUrl.Host}")
            };

            base.SetWalletInfo(wallet, publicKey);
        }

        public override async Task OptionsAsync(CancellationToken token)
        {
            await _poolSettings.LoadAsync();

            await RefreshStakeBalancesAsync(token);

            if(token.IsCancellationRequested)
            {
                return;
            }

            await DisplayOptionsAsync(token);
        }

        private async Task RefreshStakeBalancesAsync(CancellationToken token)
        {
            await AnsiConsole.Status().StartAsync($"Grabbing balance information", async ctx =>
            {
                var balanceInfo = await GetBalanceAsync(token);
                _minerInformation.Balance = balanceInfo.success ? balanceInfo.balance : _minerInformation.Balance;
                _minerInformation.UpdateStakes(await GetStakingInformationAsync(token));
            });
        }

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

                builder.AppendLine($"{"Wallet Balance".PadRight(14)} {_minerInformation.Balance} ore");
                builder.AppendLine($"{"Total Rewards".PadRight(14)} {_minerInformation.TotalRewards:0.00000000000} ore {(_minerInformation.ChangeSinceRefresh > 0 ? $"([green]+{_minerInformation.ChangeSinceRefresh:0.00000000000} in {_minerInformation.TimeSinceLastUpdate.TotalMinutes:0.00}m[/])" : String.Empty)}");

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

                SelectionPrompt<string> choices = new SelectionPrompt<string>();
                choices.Title(builder.ToString());

                const string claimBalance = "Claim Balance";
                const string changeWallet = "Change Claim Wallet";
                const string refreshBalance = "Refresh Balance";

                choices.AddChoice(claimBalance);
                choices.AddChoice(refreshBalance);
                choices.AddChoice(changeWallet);
                choices.AddChoice("Exit");

                string choice = await choices.ShowAsync(AnsiConsole.Console, token);

                switch (choice)
                {
                    case refreshBalance:
                        await RefreshStakeBalancesAsync(token);
                        break;
                    case changeWallet:
                        await SetClaimWalletAsync(token);
                        break;
                    default:
                        return;
                }

                AnsiConsole.Clear();
            }

        }
        
        private async Task SetClaimWalletAsync(CancellationToken token)
        {
            Base58Encoder encoder = new Base58Encoder();

            while(true)
            {
                TextPrompt<string> textPrompt = new TextPrompt<string>("Set wallet to claim rewards to. Type [yellow]clear[/] to remove. Default: ");
                textPrompt.DefaultValue(_poolSettings.ClaimWallet);
                textPrompt.Validate((str) =>
                {
                    if(str.ToLower() == "clear")
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

                if(result?.ToLower() == "clear")
                {
                    _poolSettings.ClaimWallet = null;
                }
                else
                {
                    _poolSettings.ClaimWallet = result;
                }

                await _poolSettings.SaveAsync();

                break;
            }
        }

        public override async Task<bool> SetupAsync(CancellationToken token, bool initialSetup = false)
        {
            bool isComplete = true;

            await AnsiConsole.Status().StartAsync($"Setting up {PoolName} pool", async ctx =>
            {
                if(_wallet == null)
                {
                    AnsiConsole.MarkupLine("[red]A full keypair is required to sign message for this pool. Private keys are never sent to the server[/]\n");

                    isComplete = false;
                }
                else
                {
                    try
                    {
                        if(!await RegisterAsync(token))
                        {
                            AnsiConsole.MarkupLine($"[red]Failed to signup to pool[/]\n");

                            isComplete = false;
                        }
                    }
                    catch(Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Failed to complete setup. Reason: {ex.Message}[/]\n");
                    }
                }
            });

            return isComplete;
        }

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
        
        public override string[] TableHeaders()
        {
            return ["Date", "Diff", "Ore", "Pool Diff", "Pool Ore", "Miner %"];
        }

        private void HandleNewChallenge(ChallengeResponse challengeResponse)
        {
            _currentBestDifficulty = 0;
            _challenge = challengeResponse.Challenge;

            OnChallengeUpdate?.Invoke(this, new NewChallengeInfo
            {
                ChallengeId = Interlocked.Increment(ref _challengeId),
                Challenge = challengeResponse.Challenge,
                StartNonce = challengeResponse.StartNonce,
                EndNonce = challengeResponse.EndNonce
            });
        }

        private void HandleSubmissionResult(PoolSubmissionResponse submissionResponse)
        {
            _logger.Log(LogLevel.Error, $"GOT POOL RESULTS");
        }

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

        private class MinerPoolInformation
        {
            public double Balance { get; set; }
            public List<PoolStake> Stakes { get; set; }

            public double TotalRewards => Stakes?.Sum(x => x.RewardsBalance / OreProgram.OreDecimals) ?? 0;
            public TimeSpan TimeSinceLastUpdate => _currentUpdate - _oldUpdate;

            public double ChangeSinceRefresh => Math.Max(TotalRewards - oldBalance, 0);
            private double oldBalance = 0;
            private DateTime _oldUpdate = DateTime.UtcNow;
            private DateTime _currentUpdate = DateTime.UtcNow;

            public void UpdateStakes(List<PoolStake> stakes)
            {
                bool isFirst = Stakes == null;

                _oldUpdate = _currentUpdate;
                _currentUpdate = DateTime.Now;

                oldBalance = TotalRewards;
                Stakes = stakes;

                if (isFirst)
                {
                    _oldUpdate = DateTime.UtcNow;
                    oldBalance = TotalRewards;
                }
            }
        }

        private class HQPoolSettings : PoolSettings
        {
            public string ClaimWallet { get; set; }

            public HQPoolSettings(string poolName) : base(poolName)
            {
            }
        }
    }
}
