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
        public DateTimeOffset ExpiresAt { get; private set; }
        public PublicKey Mint { get; private set; }
        public ulong Multiplier { get; private set; }
        public Fraction RewardsFactor { get; private set; }
        public ulong TotalDeposits { get; private set; }
        public ulong TotalStakers { get; private set; }
        public ulong WithdrawFee { get; private set; }
        //1024 byte buffer

        public static Boost Deserialize(ReadOnlySpan<byte> data)
        {
            return new Boost
            {
                ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(data.GetS64(8)),
                Mint = data.GetPubKey(16),
                Multiplier = data.GetU64(48),
                RewardsFactor = data.GetFraction(56),
                TotalDeposits = data.GetU64(72),
                TotalStakers = data.GetU64(80),
                WithdrawFee = data.GetU64(88)
            };
        }
    }
}
