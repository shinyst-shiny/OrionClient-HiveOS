using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools.Models
{
    public interface IMessage
    {
        public void Deserialize(ArraySegment<byte> data);
        public ArraySegment<byte> Serialize();
    }
}
