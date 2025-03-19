using OrionClientLib.CoinPrograms;
using OrionClientLib.CoinPrograms.Ore;
using Solnet.Programs.Utilities;
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
        public Boost BoostInfo { get; set; }
        public PublicKey StakeAccount { get; private set; }
        public PublicKey Authority { get; private set; }

        public double Multiplier { get; set; }

        public decimal TotalLPStake { get; set; }
        public decimal TotalOreStaked { get; set; }
        public decimal TotalTokenBStaked { get; set; }


        public double TotalBoostStake { get; set; }
        public double UserStake { get; set; }
        public double Rewards { get; set; }
        public decimal OreUSDValue { get; set; }
        public decimal TokenBUSDValue { get; set; }
        public ulong ProofBalance { get; set; }

        public ulong TotalStakers { get; set; }
        public DateTime LastUpdated { get; set; }
        public DateTime LastPayout { get; set; }
        public bool Enabled { get; set; }
        public bool Locked { get; set; }

        public decimal BoostRatioStaked => TotalLPStake == 0 ? 0 : (decimal)TotalBoostStake / TotalLPStake;
        public decimal TradingVolumeLocked => TotalOreStaked * OreUSDValue + TotalTokenBStaked * TokenBUSDValue;
        public decimal ShareUSDValue => TotalLPStake == 0 ? 0 : TradingVolumeLocked / TotalLPStake;

        public decimal BoostTotalUSDValue => TradingVolumeLocked * BoostRatioStaked;
        public decimal UserStakeUSDValue => (decimal)(UserStake) * ShareUSDValue;
        public double SharePercent => UserStake == 0 ? 0 : UserStake / TotalBoostStake * 100;

        public decimal RewardUSDValue => OreUSDValue * (decimal)Rewards;

        public StakeInformation(BoostInformation boost, PublicKey authority)
        {
            Boost = boost;
            Authority = authority;
            StakeAccount = OreProgram.DeriveStakeAccount(Boost.BoostAddress, Authority);
        }
    }
}
