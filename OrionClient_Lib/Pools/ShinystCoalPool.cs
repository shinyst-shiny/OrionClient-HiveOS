using Newtonsoft.Json;
using NLog;
using OrionClientLib.Pools.CoalPool;
using OrionClientLib.Pools.HQPool;
using Solnet.Wallet.Utilities;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools
{
    public class ShinystCoalPool : OreHQPool
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public override event EventHandler<string[]> OnMinerUpdate;
        public override string Name { get; } = "Coal Pool";
        public override string DisplayName => Name;
        public override bool Display => true;
        public override string Description => $"[Cyan]{Coin.Coal}[/]/[green]{Coin.Ore}[/] pool using Ore-HQ implementation. Operator (discord): Shinyst";
        public override Coin Coins { get; } = Coin.Coal;

        public override Dictionary<string, string> Features { get; } = new Dictionary<string, string>();

        public override bool HideOnPoolList { get; } = false;
        public override string HostName { get; protected set; } = "pool.coal-pool.xyz";

        public override Dictionary<Coin, double> MiniumumRewardPayout { get; } = new Dictionary<Coin, double> { { Coin.Coal, 1 }, { Coin.Ore, 0.05 } };

        protected override async Task<(bool success, string message)> ClaimMiningRewards(Coin coin, string claimWallet, ulong amount, CancellationToken token)
        {
            try
            {
                if (!await UpdateTimestampAsync(token))
                {
                    return (false, $"Failed to get timestamp from server");
                }

                Base58Encoder _encoder = new Base58Encoder();

                byte[] tBytes = new byte[8 + 32 + 8 + 8];
                BinaryPrimitives.WriteUInt64LittleEndian(tBytes, _timestamp);
                _encoder.DecodeData(claimWallet).CopyTo(tBytes, 8);

                if (coin == Coin.Coal)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(tBytes.AsSpan().Slice(40, 8), amount);
                }
                else if (coin == Coin.Ore)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(tBytes.AsSpan().Slice(48, 8), amount);
                }

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

        protected override async Task<(List<(Coin, double)> value, bool success)> GetDataAsync(string endpoint, CancellationToken token)
        {
            List<(Coin, double)> balanceData = new List<(Coin, double)>();

            try
            {
                using var response = await _client.GetAsync($"/miner/{endpoint}?pubkey={_publicKey}", token);

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    return (balanceData, false);
                }

                string data = await response.Content.ReadAsStringAsync();

                var coinValues = JsonConvert.DeserializeObject<Dictionary<Coin, double>>(data);

                foreach(var kvp in coinValues)
                {
                    balanceData.Add((kvp.Key, kvp.Value));
                }

                return (balanceData, true);
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Warn, $"Failed to request {endpoint} data from pool. Reason: {ex.Message}");

                return (balanceData, false);
            }
        }

        protected override async void HandleSubmissionResult(ArraySegment<byte> buffer)
        {
            CoalHQPoolSubmissionResponse submissionResponse = new CoalHQPoolSubmissionResponse();
            submissionResponse.Deserialize(buffer);

            CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            OnMinerUpdate?.Invoke(this, [
                DateTime.Now.ToShortTimeString(),
                GenerateChallengeId(submissionResponse.Challenge).ToString(),
                $"{submissionResponse.CoalDetail.RewardDetails.MinerSuppliedDifficulty}/{submissionResponse.Difficulty}",
                $"{submissionResponse.CoalDetail.RewardDetails.MinerEarnedRewards:0.00000000000}/{submissionResponse.OreDetail.RewardDetails.MinerEarnedRewards:0.00000000000}",
                //$"{_minerInformation.TotalStakeRewards.BalanceChangeSinceUpdate:0.00000000000}",
                $"{submissionResponse.CoalDetail.RewardDetails.TotalRewards:0.00000000000}/{submissionResponse.OreDetail.RewardDetails.TotalRewards:0.00000000000}",
                $"{_minerInformation.TotalMiningRewards[Coin.Coal]:0.00000000000}/{_minerInformation.TotalMiningRewards[Coin.Ore]:0.00000000000}",
                //$"{_minerInformation.TotalStakeRewards:0.00000000000}",
            ]);
        }

        public override string[] TableHeaders()
        {
            return ["Time", "Id", "Diff", "Mining Rewards (Coal/Ore)", $"Pool Rewards", "Unclaimed Rewards"];
        }
    }
}
