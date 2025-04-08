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
        public I80F48Fraction LastRewardsFactor { get; private set; }
        public PublicKey Mint { get; private set; }
        public I80F48Fraction RewardsFactor { get; private set; }
        public ulong TotalDeposits { get; private set; }
        public ulong TotalStakers { get; private set; }
        public ulong Weight { get; private set; }
        public ulong WithdrawFee { get; private set; }
        //1024 byte buffer

        public static Boost Deserialize(ReadOnlySpan<byte> data)
        {
            return new Boost
            {
                ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(data.GetS64(8)),
                LastRewardsFactor = data.GetI80F48Fraction(16),
                Mint = data.GetPubKey(32),
                RewardsFactor = data.GetI80F48Fraction(64),
                TotalDeposits = data.GetU64(80),
                TotalStakers = data.GetU64(88),
                Weight = data.GetU64(96),
                WithdrawFee = data.GetU64(104)
            };
        }
    }
}
