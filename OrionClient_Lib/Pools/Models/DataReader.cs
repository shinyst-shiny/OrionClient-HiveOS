using Solnet.Wallet;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools.Models
{
    public class DataReader
    {
        private ArraySegment<byte> _data;
        private int _index = 0;

        public DataReader(ArraySegment<byte> data)
        {
            _data = data;
        }

        public PublicKey ReadPublicKey()
        {
            return new PublicKey(ReadBytes(32));
        }

        public byte[] ReadBytes(int number)
        {
            byte[] v = _data.Slice(_index, number).ToArray();

            _index += number;

            return v;
        }

        public double ReadDouble()
        {
            double v = BinaryPrimitives.ReadDoubleLittleEndian(_data.Slice(_index, 8));

            _index += 8;

            return v;
        }

        public uint ReadUInt()
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_index, 4));

            _index += 4;

            return v;
        }

        public ulong ReadULong()
        {
            ulong v = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_index, 8));

            _index += 8;

            return v;
        }
    }
}
