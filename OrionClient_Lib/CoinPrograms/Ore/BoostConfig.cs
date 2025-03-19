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
        public PublicKey Current { get; private set; }
        public ulong Length { get; private set; }
        public byte[] Noise { get; private set; }
        public ulong StakerTakeRate { get; private set; }
        public DateTimeOffset Timestamp { get; private set; }
        //1024 byte buffer

        public static BoostConfig Deserialize(ReadOnlySpan<byte> data)
        {
            BoostConfig config = new BoostConfig();
            config.Admin = data.GetPubKey(8);
            config.Boosts = new PublicKey[256];

            config.Length = data.GetU64(8264);
            for (int i =0; i < (int)config.Length; i++)
            {
                config.Boosts[i] = data.GetPubKey(40 + i * 32);
            }

            config.Current = data.GetPubKey(8232);

            config.Noise = data.GetBytes(8272, 32);
            config.StakerTakeRate = data.GetU64(8304);
            config.Timestamp = DateTimeOffset.FromUnixTimeSeconds(data.GetS64(8312));

            return config;
        }
    }
}
