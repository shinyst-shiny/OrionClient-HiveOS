using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DrillX
{
    public class SipRand
    {
        private SipState _key;
        private ulong _counter;

        public SipRand(SipState key)
        {
            _key = key;
            _counter = 0;
        }

        internal ulong NextU64()
        {
            var value = SipHash13Ctr(_key, _counter);

            _counter++;

            return value;
        }

        private ulong SipHash13Ctr(SipState key, ulong input)
        {
            var s = _key;

            s.V3 ^= input;
            s.SipRound();

            s.V0 ^= input;
            s.V2 ^= 0xff;

            s.SipRound();
            s.SipRound();
            s.SipRound();

            return s.V0 ^ s.V1 ^ s.V2 ^ s.V3;
        }

        internal static ulong[] SipHash24Ctr(SipState s, ulong input)
        {
            s.V1 ^= 0xee;
            s.V3 ^= input;

            s.SipRound();
            s.SipRound();

            s.V0 ^= input;
            s.V2 ^= 0xee;

            s.SipRound();
            s.SipRound();
            s.SipRound();
            s.SipRound();

            var t = s;
            t.V1 ^= 0xdd;

            t.SipRound();
            t.SipRound();
            t.SipRound();
            t.SipRound();

            return new ulong[] { s.V0, s.V1, s.V2, s.V3, t.V0, t.V1, t.V2, t.V3 };
        }
    }
}
