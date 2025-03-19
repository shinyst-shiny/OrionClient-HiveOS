using Solnet.Programs.Utilities;
using Solnet.Wallet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.CoinPrograms.Ore
{
    public class Stake
    {
        public PublicKey Authority { get; private set; }
        public ulong Balance { get; private set; }
        public PublicKey Boost { get; private set; }
        public DateTimeOffset LastClaimAt { get; private set; }
        public DateTimeOffset LastDepositAt { get; private set; }
        public DateTimeOffset LastWithdrawAt { get; private set; }
        public Fraction LastRewardsFactor { get; private set; }
        public ulong Rewards { get; private set; }

        public static Stake Deserialize(ReadOnlySpan<byte> data)
        {
            return new Stake
            {
                Authority = data.GetPubKey(8),
                Balance = data.GetU64(40),
                Boost = data.GetPubKey(48),
                LastClaimAt = DateTimeOffset.FromUnixTimeSeconds(data.GetS64(80)),
                LastDepositAt = DateTimeOffset.FromUnixTimeSeconds(data.GetS64(88)),
                LastWithdrawAt = DateTimeOffset.FromUnixTimeSeconds(data.GetS64(96)),
                LastRewardsFactor = data.GetFraction(104),
                Rewards = data.GetU64(120)
            };
        }

        public ulong CalculateRewards(Boost boost, ulong proofBalance)
        {
            decimal rewardFactor = boost.RewardsFactor.Value;
            decimal lastRewardFactor = LastRewardsFactor.Value;

            if(boost.TotalDeposits > 0)
            {
                rewardFactor += (decimal)proofBalance / boost.TotalDeposits;
            }

            if(rewardFactor > lastRewardFactor)
            {
                var accumulatedRewards = rewardFactor - lastRewardFactor;

                var personalRewards = accumulatedRewards * Balance;

                return Rewards + (ulong)personalRewards;
            }

            return Rewards;
        }
    }
}
