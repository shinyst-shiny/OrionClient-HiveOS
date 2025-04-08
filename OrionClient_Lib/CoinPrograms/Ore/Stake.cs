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
        public I80F48Fraction LastRewardsFactor { get; private set; }
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
                LastRewardsFactor = data.GetI80F48Fraction(104),
                Rewards = data.GetU64(120)
            };
        }

        public ulong CalculateRewards(Boost boost, ulong proofBalance, BoostConfig boostConfig)
        {
            var rewards = Rewards;

            decimal configRewardsFactor = boostConfig.RewardsFactor.ToDecimal();
            decimal boostRewardFactor = boost.RewardsFactor.ToDecimal();

            if(proofBalance > 0 )
            {
                configRewardsFactor += (decimal)proofBalance / boostConfig.TotalWeight;
            }

            if(configRewardsFactor > boost.LastRewardsFactor.ToDecimal())
            {
                var accumulatedRewards = configRewardsFactor - boost.LastRewardsFactor.ToDecimal();
                var boostRewards = accumulatedRewards * boost.Weight;
                boostRewardFactor += boostRewards / boost.TotalDeposits;
            }

            if(boostRewardFactor > LastRewardsFactor.ToDecimal())
            {
                var accumulatedRewards = boostRewardFactor - LastRewardsFactor.ToDecimal();
                var personalRewards = accumulatedRewards * Balance;

                rewards += (ulong)personalRewards;

            }

            return rewards;
        }
    }
}
