using Solnet.Programs.Utilities;
using Solnet.Wallet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.CoinPrograms.Ore
{
    public class Boost
    {
        public ulong Bump { get; private set; }
        public DateTimeOffset ExpiresAt { get; private set; }
        public ulong Locked { get; private set; }
        public PublicKey Mint { get; private set; }
        public ulong Multiplier { get; private set; }
        public ulong TotalDeposits { get; private set; }
        public ulong TotalStakers { get; private set; }

        public static Boost Deserialize(ReadOnlySpan<byte> data)
        {
            return new Boost
            {
                Bump = data.GetU64(8),
                ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(data.GetS64(16)),
                Locked = data.GetU64(24),
                Mint = data.GetPubKey(32),
                Multiplier = data.GetU64(64),
                TotalDeposits = data.GetU64(72),
                TotalStakers = data.GetU64(80)
            };
        }
    }
}
