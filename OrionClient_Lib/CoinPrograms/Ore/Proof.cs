using Solnet.Programs.Utilities;
using Solnet.Rpc.Models;
using Solnet.Wallet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.CoinPrograms.Ore
{
    public class Proof
    {
        public PublicKey Authority { get; private set; }
        public ulong Balance { get; set; } //Stake
        public byte[] Hash { get; set; }
        public byte[] LastHash { get; set; }
        public DateTimeOffset LastHashAt { get; set; }
        public DateTimeOffset LastStakeAt { get; set; }
        public PublicKey MiningAuthority { get; set; }
        public ulong TotalHashes { get; set; }
        public ulong TotalRewards { get; set; }

        public static Proof Deserialize(ReadOnlySpan<byte> data)
        {
            return new Proof
            {
                Authority = data.GetPubKey(8),
                Balance = data.GetU64(40),
                Hash = data.GetBytes(48, 32),
                LastHash = data.GetBytes(80, 32),
                LastHashAt = DateTimeOffset.FromUnixTimeSeconds(data.GetS64(112)),
                LastStakeAt = DateTimeOffset.FromUnixTimeSeconds(data.GetS64(120)),
                MiningAuthority = data.GetPubKey(128),
                TotalHashes = data.GetU64(160),
                TotalRewards = data.GetU64(168)
            };
        }
    }
}
