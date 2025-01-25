using Solnet.Programs.Utilities;
using Solnet.Wallet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.CoinPrograms.Ore
{
    public class BoostDirectory
    {
        public PublicKey[] Boosts { get; private set; } = new PublicKey[256];
        public ulong ActiveBoosts { get; private set; }

        public static BoostDirectory Deserialize(ReadOnlySpan<byte> data)
        {
            BoostDirectory directory = new BoostDirectory
            {
                Boosts = new PublicKey[256],
            };

            for(int i =0; i < directory.Boosts.Length; i++)
            {
                directory.Boosts[i] = data.GetPubKey(8 + i * 32);
            };

            directory.ActiveBoosts = data.GetU64(directory.Boosts.Length * 32 + 8);

            return directory;
        }
    }
}
