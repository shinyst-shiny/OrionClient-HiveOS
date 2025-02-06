using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionEventLib
{
    public class EventDeserializer
    {
        private ArraySegment<byte> _data;
        private int _index = 0;

        public EventDeserializer(ArraySegment<byte> data)
        {
            _data = data;
        }

        public void Seek(int offset)
        {
            _index = offset;
        }

        public void Skip(int count)
        {
            _index += count;
        }

        public byte ReadByte()
        {
            return _data[_index++];
        }

        public bool ReadBool()
        {
            return ReadByte() == 1;
        }

        public double ReadDouble()
        {
            double v = BinaryPrimitives.ReadDoubleLittleEndian(_data.Slice(_index, 8));

            _index += 8;

            return v;
        }

        public byte[] ReadBytes()
        {
            int count = ReadU16();

            byte[] v = _data.Slice(_index, count).ToArray();

            _index += count;

            return v;
        }

        public ushort ReadU16()
        {
            ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_index, 2));

            _index += 2;

            return v;
        }

        public uint ReadU32()
        {
           uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_index, 4));

            _index += 4;

            return v;
        }

        public int ReadS32()
        {
            int v = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_index, 4));

            _index += 4;

            return v;
        }

        public ulong ReadU64()
        {
            ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_index, 8));

            _index += 8;

            return v;
        }

        public long ReadS64()
        {
            long v = BinaryPrimitives.ReadInt64LittleEndian(_data.Slice(_index, 8));

            _index += 8;

            return v;
        }

        public string ReadString()
        {
            return Encoding.UTF8.GetString(ReadBytes());
        }
    }
}
