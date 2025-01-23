using Solnet.Programs.Utilities;
using Solnet.Wallet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.CoinPrograms.Ore
{
    public class CheckPoint
    {
        public PublicKey Boost { get; private set; }
        public ulong CurrentId { get; private set; }
        public ulong TotalPendingDeposits { get; private set; }
        public ulong TotalStakers { get; private set; }
        public ulong TotalRewards { get; private set; }
        public DateTimeOffset LastCheckPoint { get; private set; }

        public static CheckPoint Deserialize(ReadOnlySpan<byte> data)
        {
            return new CheckPoint
            {
                Boost = data.GetPubKey(8),
                CurrentId = data.GetU64(40),
                TotalPendingDeposits = data.GetU64(48),
                TotalStakers = data.GetU64(56),
                TotalRewards = data.GetU64(64),
                LastCheckPoint = DateTimeOffset.FromUnixTimeSeconds(data.GetS64(72))
            };
        }
    }
}
