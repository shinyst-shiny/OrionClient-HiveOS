
using System.Buffers.Binary;

namespace DrillX.Compiler
{
    internal class RngBuffer
    {
        private SipRand _inner;
        private byte[] _u8Vec = new byte[8];

        private int _index = -1;
        private byte _last => _u8Vec[_index];

        private uint? _u32Opt = null;

        public RngBuffer(SipRand rng)
        {
            _inner = rng;
        }

        internal byte NextByte()
        {
            if(_index >= 0)
            {
                var v = _last;
                --_index;

                return v;
            }

            BinaryPrimitives.WriteUInt64LittleEndian(_u8Vec, _inner.NextU64());

            var last = _u8Vec[7];
            _index = 6;


            return last;
        }

        internal uint NextU32()
        {
            if(_u32Opt.HasValue)
            {
                var v = _u32Opt.Value;

                _u32Opt = null;

                return v;
            }

            var value = _inner.NextU64();
            _u32Opt = (uint)value;

            return (uint)(value >> 32);
        }
    }
}