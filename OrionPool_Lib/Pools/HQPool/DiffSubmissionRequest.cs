using OrionClientLib.Pools.Models;
using Solnet.Programs.Utilities;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools.HQPool
{
    internal class DiffSubmissionRequest : IMessage
    {
        public byte[] Digest { get; set; }
        public ulong Nonce { get; set; }
        public byte[] PublicKey { get; set; }
        public byte[] Signature { get; set; }

        public void Deserialize(ArraySegment<byte> data)
        {
            throw new NotImplementedException();
        }

        public ArraySegment<byte> Serialize()
        {
            byte[] data = new byte[1 + 16 + 8 + 32 + 64];

            data[0] = 2;
            data.WriteSpan(Digest, 1);
            data.WriteU64(Nonce, 17);
            data.WriteSpan(PublicKey, 25);
            data.WriteSpan(Signature, 57);

            return data;
        }
    }
}
