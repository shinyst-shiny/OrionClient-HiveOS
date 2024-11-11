using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools.HQPool
{
    public class OreHQPoolStake
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("pool_id")]
        public int PoolId { get; set; }

        [JsonProperty("mint_pubkey")]
        public string MintPubkey { get; set; }

        [JsonProperty("staker_pubkey")]
        public string StakerPubkey { get; set; }

        [JsonProperty("stake_pda")]
        public string StakePda { get; set; }

        [JsonProperty("rewards_balance")]
        public long RewardsBalance { get; set; }

        [JsonProperty("staked_balance")]
        public long StakedBalance { get; set; }

        public string PoolName { get; set; }
        public double Decimals { get; set; }
        public double RewardsUI => RewardsBalance / Decimals;
    }
}
