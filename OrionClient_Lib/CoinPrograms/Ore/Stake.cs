using Solnet.Programs.Utilities;
using Solnet.Wallet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.CoinPrograms.Ore
{
    public class Stake
    {
        public PublicKey Authority { get; private set; }
        public ulong Balance { get; private set; }
        public ulong BalancePending { get; private set; }
        public PublicKey Boost { get; private set; }
        public ulong Id { get; private set; }
        public ulong LastDepositAt { get; private set; }
        public ulong Rewards { get; private set; }

        public static Stake Deserialize(ReadOnlySpan<byte> data)
        {
            return new Stake
            {
                Authority = data.GetPubKey(8),
                Balance = data.GetU64(40),
                BalancePending = data.GetU64(48),
                Boost = data.GetPubKey(56),
                Id = data.GetU64(88),
                LastDepositAt = data.GetU64(96),
                Rewards = data.GetU64(104)
            };
        }
    }
}
