using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules.Staking
{
    internal class MeteroaPoolInfo
    {
        [JsonProperty("pool_address")]
        public string PoolAddress { get; set; }

        [JsonProperty("pool_token_mints")]
        public List<string> PoolTokenMints { get; set; }

        [JsonProperty("pool_token_amounts")]
        public List<decimal> PoolTokenAmounts { get; set; }

        [JsonProperty("pool_token_usd_amounts")]
        public List<decimal> PoolTokenUsdAmounts { get; set; }

        [JsonProperty("lp_mint")]
        public string LpMint { get; set; }

        [JsonProperty("pool_tvl")]
        public decimal PoolTvl { get; set; }

        [JsonProperty("pool_name")]
        public string PoolName { get; set; }

        [JsonProperty("lp_decimal")]
        public int LpDecimal { get; set; }

        [JsonProperty("pool_lp_price_in_usd")]
        public decimal PoolLpPriceInUsd { get; set; }
    }
}
