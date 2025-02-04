using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionEventLib
{
    public enum SerializationType { Binary, Json };

    public class EventSerializer
    {
        public ArraySegment<byte> Data => GetData();
        public int BytesWritten { get; private set; }
        private ArraySegment<byte> _data;
        private int _index => BytesWritten + 2; //2 bytes for size
        public EventSerializer(ArraySegment<byte> data)
        {
            _data = data;
        }

        public ArraySegment<byte> GetData()
        {
            //Size
            BinaryPrimitives.WriteUInt16LittleEndian(_data.Slice(0, 2), (ushort)BytesWritten);

            return _data.Slice(0, _index);
        }

        public void WriteByte(byte v)
        {
            Data.Slice(_index, 1)[0] = v;
            BytesWritten++;
        }

        public void WriteBool(bool v)
        {
            WriteByte(v ? (byte)1 : (byte)0);
        }

        public void WriteDouble(double v)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(Data.Slice(_index, 8), v);

            BytesWritten += 8;
        }

        public void WriteBytes(ReadOnlySpan<byte> v)
        {
            v.CopyTo(Data.Slice(_index, v.Length));
            BytesWritten += v.Length;
        }

        public void WriteU16(ushort v)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(Data.Slice(_index, 2), v);

            BytesWritten += 2;
        }

        public void WriteU32(uint v)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(Data.Slice(_index, 4), v);

            BytesWritten += 4;
        }

        public void WriteS32(int v)
        {
            BinaryPrimitives.WriteInt32LittleEndian(Data.Slice(_index, 4), v);

            BytesWritten += 4;
        }

        public void WriteU64(ulong v)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(Data.Slice(_index, 8), v);

            BytesWritten += 8;
        }

        public void WriteS64(long v)
        {
            BinaryPrimitives.WriteInt64LittleEndian(Data.Slice(_index, 8), v);

            BytesWritten += 8;
        }

        public void WriteString(string v)
        {
            if (String.IsNullOrEmpty(v))
            {
                WriteU16(0);
            }
            else
            {
                WriteU16((ushort)v.Length);
                WriteBytes(Encoding.UTF8.GetBytes(v));
            }
        }
    }
}
