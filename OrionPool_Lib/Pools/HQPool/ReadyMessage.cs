using OrionClientLib.Pools.Models;
using Solnet.Programs.Utilities;
using Solnet.Wallet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools.HQPool
{
    internal class ReadyRequestMessage : IMessage
    {
        public byte Type { get; private set; } = 0;
        public PublicKey PublicKey { get; set; }
        public ulong Timestamp { get; set; }
        public byte[] Signature { get; set; }

        public void Deserialize(ArraySegment<byte> data)
        {
            throw new NotImplementedException();
        }

        public ArraySegment<byte> Serialize()
        {
            byte[] data = new byte[1 + 32 + 8 + 64];

            data.WriteU8(Type, 0);
            data.WriteSpan(PublicKey.KeyBytes, 1);
            data.WriteU64(Timestamp, 33);
            data.WriteSpan(Signature, 41);

            return data;
        }
    }
}
