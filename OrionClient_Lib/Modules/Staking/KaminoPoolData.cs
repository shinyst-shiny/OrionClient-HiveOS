using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules.Staking
{
    public class KaminoPoolData
    {
        [JsonProperty("strategy")]
        public string Strategy { get; set; }

        [JsonProperty("tokenAMint")]
        public string TokenAMint { get; set; }

        [JsonProperty("tokenBMint")]
        public string TokenBMint { get; set; }

        [JsonProperty("tokenA")]
        public string TokenA_ { get; set; }

        [JsonProperty("tokenB")]
        public string TokenB_ { get; set; }

        [JsonProperty("sharePrice")]
        public decimal SharePrice { get; set; }

        [JsonProperty("sharesIssued")]
        public decimal SharesIssued { get; set; }

        [JsonProperty("totalValueLocked")]
        public decimal TotalValueLocked { get; set; }

        [JsonProperty("vaultBalances")]
        public VaultBalances VaultBalances_ { get; set; }

        public class Token
        {
            [JsonProperty("invested")]
            public decimal Invested { get; set; }

            [JsonProperty("available")]
            public decimal Available { get; set; }

            [JsonProperty("total")]
            public decimal Total { get; set; }

            [JsonProperty("totalUsd")]
            public decimal TotalUsd { get; set; }

            public decimal TokenUSDValue => TotalUsd / Total;
        }

        public class VaultBalances
        {
            [JsonProperty("tokenA")]
            public Token TokenA { get; set; }

            [JsonProperty("tokenB")]
            public Token TokenB { get; set; }

        }
    }
}
