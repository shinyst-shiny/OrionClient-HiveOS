using Solnet.Programs.Utilities;
using Solnet.Wallet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.CoinPrograms.Ore
{
    public class BoostConfig
    {
        public PublicKey Admin { get; private set; }
        public PublicKey[] Boosts { get; private set; }
        public ulong Length { get; private set; }
        public ulong TakeRate { get; private set; }
        public ulong TotalWeight { get; private set; }
        public I80F48Fraction RewardsFactor { get; private set; }
        
        //1024 byte buffer

        public static BoostConfig Deserialize(ReadOnlySpan<byte> data)
        {
            BoostConfig config = new BoostConfig();
            config.Admin = data.GetPubKey(8);
            config.Boosts = new PublicKey[256];

            for (int i =0; i < config.Boosts.Length; i++)
            {
                config.Boosts[i] = data.GetPubKey(40 + i * 32);
            }

            config.Length = data.GetU64(40 + 256 * 32);
            config.RewardsFactor = data.GetI80F48Fraction(40 + 256 * 32 + 8);

            config.TakeRate = data.GetU64(40 + 256 * 32 + 8 + 16);
            config.TotalWeight = data.GetU64(40 + 256 * 32 + 8 + 16 + 8);

            return config;
        }
    }
}
