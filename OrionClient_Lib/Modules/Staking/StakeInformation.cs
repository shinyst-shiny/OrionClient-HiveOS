using OrionClientLib.CoinPrograms;
using OrionClientLib.CoinPrograms.Ore;
using Solnet.Wallet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules.Staking
{
    public class StakeInformation
    {
        public BoostInformation Boost { get; private set; }
        public PublicKey StakeAccount { get; private set; }
        public PublicKey Authority { get; private set; }

        public double Multiplier { get; set; }

        public double TotalStake { get; set; }
        public double UserStake { get; set; }
        public double PendingStake { get; set; }
        public double Rewards { get; set; }
        public double PendingRewards { get; set; }
        public decimal OreUSDValue { get; set; }
        public decimal TokenBUSDValue { get; set; }
        public decimal LPUSDValue { get; set; }
        public ulong TotalStakers { get; set; }
        public DateTime LastUpdated { get; set; }
        public DateTime LastPayout { get; set; }

        public decimal LPTotalUSDValue => LPUSDValue * (decimal)TotalStake;
        public decimal UserStakeUSDValue => (decimal)(UserStake) * LPUSDValue;
        public decimal UserPendingStakeUSDValue => (decimal)(PendingStake) * LPUSDValue;
        public double SharePercent => UserStake == 0 ? 0 : UserStake / TotalStake * 100;
        public decimal RewardUSDValue => OreUSDValue * (decimal)Rewards;

        public double UserPendingRewards => SharePercent / 100 * PendingRewards;
        public decimal PendingUserRewardUSDValue => OreUSDValue * (decimal)UserPendingRewards;

        public StakeInformation(BoostInformation boost, PublicKey authority)
        {
            Boost = boost;
            Authority = authority;
            StakeAccount = OreProgram.DeriveStakeAccount(Boost.BoostAddress, Authority);
        }
    }
}
