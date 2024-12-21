using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules.Vanity
{
    //https://github.com/firedancer-io/firedancer/blob/main/src/ballet/base58/fd_base58_avx.h
    public class Base58EncoderPerf
    {
        private const int N = 32;
        private const int IntermediateSZ = 9;
        private const int IntermediateSZWithPadding = 12;
        private const int BinarySZ = (int)(N / 4UL);
        private const char Base58InvalidChar = (char)255;
        public static readonly char[] PszBase58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz".ToCharArray();

        private const char Base58InverseTableOffset = (char)'1';
        private const char Base58InverseTableSentinal = (char)(1 + 'z' - Base58InverseTableOffset);

        private static readonly char[] Base58Inverse = new char[] {
              (char)  0, (char)  1, (char)  2, (char)  3, (char)  4, (char)  5, (char)  6, (char)  7, (char)  8, (char)255,
              (char)255, (char)255, (char)255, (char)255, (char)255, (char)255, (char)  9, (char) 10, (char) 11, (char) 12,
              (char) 13, (char) 14, (char) 15, (char) 16, (char)255, (char) 17, (char) 18, (char) 19, (char) 20, (char) 21,
              (char)255, (char) 22, (char) 23, (char) 24, (char) 25, (char )26, (char) 27, (char) 28, (char) 29, (char) 30,
              (char) 31, (char) 32, (char)255, (char)255, (char)255, (char)255, (char)255, (char)255, (char) 33, (char) 34,
              (char) 35, (char) 36, (char) 37, (char) 38, (char) 39, (char) 40, (char) 41, (char) 42, (char) 43, (char)255,
              (char) 44, (char) 45, (char) 46, (char) 47, (char) 48, (char) 49, (char) 50, (char) 51, (char) 52, (char) 53,
              (char) 54, (char) 55, (char) 56, (char) 57, (char)255
        };

        private static readonly Vector256<uint>[] EncTable32 = new Vector256<uint>[8]{
          Vector256.Create(new uint[] {   513735U,  77223048U, 437087610U, 300156666U, 605448490U, 214625350U, 141436834U, 379377856U}),
          Vector256.Create(new uint[] {        0U,     78508U, 646269101U, 118408823U,  91512303U, 209184527U, 413102373U, 153715680U}),
          Vector256.Create(new uint[] {        0U,         0U,     11997U, 486083817U,   3737691U, 294005210U, 247894721U, 289024608U}),
          Vector256.Create(new uint[] {        0U,         0U,         0U,      1833U, 324463681U, 385795061U, 551597588U,  21339008U}),
          Vector256.Create(new uint[] {        0U,         0U,         0U,         0U,       280U, 127692781U, 389432875U, 357132832U}),
          Vector256.Create(new uint[] {        0U,         0U,         0U,         0U,         0U,        42U, 537767569U, 410450016U}),
          Vector256.Create(new uint[] {        0U,         0U,         0U,         0U,         0U,         0U,         6U, 356826688U}),
          Vector256.Create(new uint[] {        0U,         0U,         0U,         0U,         0U,         0U,         0U,         1U})
        };

        private static readonly Vector256<ulong> _cA = Vector256.Create(2369637129ul);
        private static readonly Vector256<ulong> _cB = Vector256.Create(1307386003ul);
        private static readonly Vector256<ulong> _58 = Vector256.Create(58ul);
        private static readonly Vector256<long> _compare = Vector256.Create(0L, 1L, 2L, 3L);
        private static readonly Vector256<byte> _shuffle1 = Vector256.Create((byte)0, 1, 1, 1, 1, 8, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                                                                                   0, 1, 1, 1, 1, 8, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1);
        private static readonly Vector256<byte> _shuffle1_2 = Vector256.Create((byte)1, 0, 1, 1, 1, 1, 8, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                                                                                   0, 1, 1, 1, 1, 8, 1, 1, 1, 1, 1, 1, 1, 1, 1);
        private static readonly Vector256<byte> _shuffle1_3 = Vector256.Create((byte)1, 1, 0, 1, 1, 1, 1, 8, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                                                                                   0, 1, 1, 1, 1, 8, 1, 1, 1, 1, 1, 1, 1, 1);
        private static readonly Vector256<byte> _shuffle1_4 = Vector256.Create((byte)1, 1, 1, 0, 1, 1, 1, 1, 8, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                                                                                   0, 1, 1, 1, 1, 8, 1, 1, 1, 1, 1, 1, 1);
        private static readonly Vector256<byte> _shuffle1_5 = Vector256.Create((byte)1, 1, 1, 1, 0, 1, 1, 1, 1, 8, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                                                                                   0, 1, 1, 1, 1, 8, 1, 1, 1, 1, 1, 1);

        private static readonly Vector256<byte> _shuffle2 = Vector256.Create((byte)3, 2, 1, 0, 7, 6, 5, 4, 11, 10, 9, 8, 15, 14, 13, 12, 19, 18, 17, 16, 23, 22, 21, 20, 27, 26, 25, 24, 31, 30, 29, 28);

        private static readonly Vector256<uint>[] EncTable32_Wide = new Vector256<uint>[16];

        static Base58EncoderPerf()
        {
            if (!BitConverter.IsLittleEndian)
            {
                throw new Exception("Big endian is not supported");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<byte> IntermediateToRawV2(Vector256<ulong> div0)
        {
            /* The computation we need to do here mathematically is
             y=(floor(x/58^k) % 58) for various values of k.  It seems that the
             best way to compute it (at least what the compiler generates in the
             scalar case) is by computing z = floor(x/58^k). y = z -
             58*floor(z/58).  Simplifying, gives, y = floor(x/58^k) -
             58*floor(x/58^(k+1)) (Note, to see that the floors simplify like
             that, represent x in its base58 expansion and then consider that
             dividing by 58^k is just shifting right by k places.) This means we
             can reuse a lot of values!

             We can do the divisions with "magic multiplication" (i.e. multiply
             and shift).  There's a tradeoff between ILP and register pressure
             to make here: we can load a constant for each value of k and just
             compute the division directly, or we could use one constant for
             division by 58 and apply it repeatedly.  I don't know if this is
             optimal, but I use two constants, one for /58 and the other for
             /58^2.  We need to take advantage of the fact the input is
             <58^5<2^32 to produce constants that fit in uints so that we can
             use mul_epu32. */

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static Vector256<ulong> Div58(Vector256<ulong> r)
            {
                return Avx2.ShiftRightLogical(Avx2.Multiply(r.AsUInt32(), _cA.AsUInt32()), 37);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static Vector256<ulong> DIV3364(Vector256<ulong> r)
            {
                return Avx2.ShiftRightLogical(Avx2.Multiply(Avx2.ShiftRightLogical(r, 2).AsUInt32(), _cB.AsUInt32()), 40);
            }

            //<0, 1, 43, 39, 26, 27, 6, 25, 13, 17, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0>
            var div1 = Div58(div0);
            var rem0 = Avx2.Subtract(div0, Avx2.Multiply(div1.AsUInt32(), _58.AsUInt32()));

            var div2 = DIV3364(div0);
            var rem1 = Avx2.Subtract(div1, Avx2.Multiply(div2.AsUInt32(), _58.AsUInt32()));

            var div3 = DIV3364(div1);
            var rem2 = Avx2.Subtract(div2, Avx2.Multiply(div3.AsUInt32(), _58.AsUInt32()));

            var div4 = DIV3364(div2);
            var rem3 = Avx2.Subtract(div3, Avx2.Multiply(div4.AsUInt32(), _58.AsUInt32()));

            var rem4 = div4;

            /*  Okay, we have all 20 terms we need at this point, but they're
                spread out over 5 registers. Each value is stored as an 8B long,
                even though it's less than 58, so 7 of those bytes are 0.  That
                means we're only taking up 4 bytes in each register.  We need to
                get them to a more compact form, but the correct order (in terms of
                place value and recalling where the input vector comes from) is:
                (letters in the right column correspond to diagram below)

                the first value in rem4  (a)
                the first value in rem3  (b)
                ...
                the first value in rem0  (e)
                the second value in rem4 (f)
                ...
                the fourth value in rem0 (t)

                The fact that moves that cross the 128 bit boundary are tricky in
                AVX makes this difficult, forcing an inconvenient output format.

                First, we'll use _mm256_shuffle_epi8 to move the second value in
                each half to byte 5:

                [ a 0 0 0 0 0 0 0  f 0 0 0 0 0 0 0 | k 0 0 0 0 0 0 0  p 0 0 0 0 0 0 0 ] ->
                [ a 0 0 0 0 f 0 0  0 0 0 0 0 0 0 0 | k 0 0 0 0 p 0 0  0 0 0 0 0 0 0 0 ]

                Then for the vectors other than rem4, we'll shuffle them the same
                way, but then shift them left (which corresponds to right in the
                picture...) and OR them together.  */

            var shift4 = Avx2.Shuffle(rem4.AsByte(), _shuffle1);
            var shift3 = Avx2.Shuffle(rem3.AsByte(), _shuffle1_2);
            var shift2 = Avx2.Shuffle(rem2.AsByte(), _shuffle1_3);
            var shift1 = Avx2.Shuffle(rem1.AsByte(), _shuffle1_4);
            var shift0 = Avx2.Shuffle(rem0.AsByte(), _shuffle1_5);
            var shift = Avx2.Or(Avx2.Or(Avx2.Or(shift4, shift3), Avx2.Or(shift2, shift1)), shift0);

            return shift;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static unsafe void Encode(ReadOnlySpan<byte> data, int index, Span<byte> output, out int start, out int end)
        {
            var _bytes = Vector256.Create(data);

            var inLeading0s = CountLeadingZeros32(_bytes);

            Span<uint> binary = stackalloc uint[12];

            _bytes = Avx2.Shuffle(_bytes, _shuffle2);

            Vector256<ulong> i0 = Vector256<ulong>.Zero;
            Vector256<ulong> i1 = Vector256<ulong>.Zero;

            ref var binaryRef = ref MemoryMarshal.GetReference(binary);

            #region Loop

            var v = Avx2.PermuteVar8x32(_bytes.AsInt32(), Vector256.Create(0x00)).AsUInt32();

            {
                i0 = Avx2.Add(i0, Avx2.Multiply(v, Vector256.Create(513735U, 0, 77223048U, 0, 437087610U, 0, 300156666U, 0)));
                i1 = Avx2.Add(i1, Avx2.Multiply(v, Vector256.Create(605448490U, 0, 214625350U, 0, 141436834U, 0, 379377856U, 0)));

                v = Avx2.PermuteVar8x32(_bytes.AsInt32(), Vector256.Create(0x01)).AsUInt32();
                i0 = Avx2.Add(i0, Avx2.Multiply(v, Vector256.Create(0U, 0, 78508U, 0, 646269101U, 0, 118408823U, 0)));
                i1 = Avx2.Add(i1, Avx2.Multiply(v, Vector256.Create(91512303U, 0, 209184527U, 0, 413102373U, 0, 153715680U, 0)));

                v = Avx2.PermuteVar8x32(_bytes.AsInt32(), Vector256.Create(0x02)).AsUInt32();
                i0 = Avx2.Add(i0, Avx2.Multiply(v, Vector256.Create(0U, 0, 0U, 0, 11997U, 0, 486083817U, 0)));
                i1 = Avx2.Add(i1, Avx2.Multiply(v, Vector256.Create(3737691U, 0, 294005210U, 0, 247894721U, 0, 289024608U, 0)));

                v = Avx2.PermuteVar8x32(_bytes.AsInt32(), Vector256.Create(0x03)).AsUInt32();
                i0 = Avx2.Add(i0, Avx2.Multiply(v, Vector256.Create(0U, 0, 0U, 0, 0U, 0, 1833U, 0)));
                i1 = Avx2.Add(i1, Avx2.Multiply(v, Vector256.Create(324463681U, 0, 385795061U, 0, 551597588U, 0, 21339008U, 0)));

                v = Avx2.PermuteVar8x32(_bytes.AsInt32(), Vector256.Create(0x04)).AsUInt32();
                i0 = Avx2.Add(i0, Avx2.Multiply(v, Vector256.Create(0U, 0, 0U, 0, 0U, 0, 0U, 0)));
                i1 = Avx2.Add(i1, Avx2.Multiply(v, Vector256.Create(280U, 0, 127692781U, 0, 389432875U, 0, 357132832U, 0)));

                v = Avx2.PermuteVar8x32(_bytes.AsInt32(), Vector256.Create(0x05)).AsUInt32();
                i0 = Avx2.Add(i0, Avx2.Multiply(v, Vector256.Create(0U, 0, 0U, 0, 0U, 0, 0U, 0)));
                i1 = Avx2.Add(i1, Avx2.Multiply(v, Vector256.Create(0U, 0, 42U, 0, 537767569U, 0, 410450016U, 0)));

                v = Avx2.PermuteVar8x32(_bytes.AsInt32(), Vector256.Create(0x06)).AsUInt32();
                i0 = Avx2.Add(i0, Avx2.Multiply(v, Vector256.Create(0U, 0, 0U, 0, 0U, 0, 0U, 0)));
                i1 = Avx2.Add(i1, Avx2.Multiply(v, Vector256.Create(0U, 0, 0U, 0, 6U, 0, 356826688U, 0)));

                var vv = Avx2.PermuteVar8x32(_bytes.AsInt32(), Vector256.Create(0x07)).AsUInt64();
                i1 = Avx2.Add(i1, Avx2.And(vv, Vector256.Create(0ul, 0, 0, 0xFFFFFFFF)));
            }

            #endregion

            Span<ulong> intermediate = stackalloc ulong[IntermediateSZWithPadding];

            i0.CopyTo(intermediate.Slice(1));
            i1.CopyTo(intermediate.Slice(5));

            // return string.Empty;

            const ulong r1Div = 656356768ul;

            ref var intermediateRef = ref MemoryMarshal.GetReference(intermediate);

            Div(8, ref Unsafe.Add(ref intermediateRef, 8), ref Unsafe.Add(ref intermediateRef, 7));
            Div(7, ref Unsafe.Add(ref intermediateRef, 7), ref Unsafe.Add(ref intermediateRef, 6));
            Div(6, ref Unsafe.Add(ref intermediateRef, 6), ref Unsafe.Add(ref intermediateRef, 5));
            Div(5, ref Unsafe.Add(ref intermediateRef, 5), ref Unsafe.Add(ref intermediateRef, 4));
            Div(4, ref Unsafe.Add(ref intermediateRef, 4), ref Unsafe.Add(ref intermediateRef, 3));
            Div(3, ref Unsafe.Add(ref intermediateRef, 3), ref Unsafe.Add(ref intermediateRef, 2));
            Div(2, ref Unsafe.Add(ref intermediateRef, 2), ref Unsafe.Add(ref intermediateRef, 1));
            Div(1, ref Unsafe.Add(ref intermediateRef, 1), ref Unsafe.Add(ref intermediateRef, 0));

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void Div(int i, ref ulong c, ref ulong p)
            {
                (ulong Quotient, ulong Remainder) = Math.DivRem(c, r1Div);

                p += Quotient;
                c = (uint)Remainder;
            }

            Span<byte> rawBase58 = stackalloc byte[128];

            #region AVX

            var iSpan = (ReadOnlySpan<ulong>)intermediate;

            var intermediate0 = Vector256.Create(iSpan); ;// new Vector<ulong>(intermediate).AsVector256();
            var intermediate1 = Vector256.Create(iSpan.Slice(4));
            var intermediate2 = Vector256.Create(iSpan.Slice(8));
            var raw0 = IntermediateToRawV2(intermediate0);
            var raw1 = IntermediateToRawV2(intermediate1);
            var raw2 = IntermediateToRawV2(intermediate2);

            TenPerSlotDown32(raw0, raw1, raw2, out var compact0, out var compact1);

            var rawLeadingZeros = CountLeadingZeros45(compact0, compact1);
            var skip = rawLeadingZeros - inLeading0s;


            var b58_0 = RawToBase58(compact0.AsSByte());
            var b58_1 = RawToBase58(compact1.AsSByte());

            b58_0.AsByte().CopyTo(output);
            b58_1.AsByte().CopyTo(output.Slice(32));

            start = skip;
            end = 45 - skip;

            #endregion
        }

        //TODO Test whether this or not having this is faster laster
        //private static readonly Vector256<sbyte> _8 = Vector256.Create((sbyte)8);
        //private static readonly Vector256<sbyte> _16 = Vector256.Create((sbyte)16);
        //private static readonly Vector256<sbyte> _21 = Vector256.Create((sbyte)21);
        //private static readonly Vector256<sbyte> _32 = Vector256.Create((sbyte)32);
        //private static readonly Vector256<sbyte> _43 = Vector256.Create((sbyte)43);
        //private static readonly Vector256<sbyte> _N7 = Vector256.Create((sbyte)-7);
        //private static readonly Vector256<sbyte> _N6 = Vector256.Create((sbyte)-6);
        //private static readonly Vector256<sbyte> _NC1 = Vector256.Create((sbyte)-'1');


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<byte> IntermediateToRaw(Vector256<ulong> div0)
        {
            /* The computation we need to do here mathematically is
             y=(floor(x/58^k) % 58) for various values of k.  It seems that the
             best way to compute it (at least what the compiler generates in the
             scalar case) is by computing z = floor(x/58^k). y = z -
             58*floor(z/58).  Simplifying, gives, y = floor(x/58^k) -
             58*floor(x/58^(k+1)) (Note, to see that the floors simplify like
             that, represent x in its base58 expansion and then consider that
             dividing by 58^k is just shifting right by k places.) This means we
             can reuse a lot of values!

             We can do the divisions with "magic multiplication" (i.e. multiply
             and shift).  There's a tradeoff between ILP and register pressure
             to make here: we can load a constant for each value of k and just
             compute the division directly, or we could use one constant for
             division by 58 and apply it repeatedly.  I don't know if this is
             optimal, but I use two constants, one for /58 and the other for
             /58^2.  We need to take advantage of the fact the input is
             <58^5<2^32 to produce constants that fit in uints so that we can
             use mul_epu32. */

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static Vector256<ulong> Div58(Vector256<ulong> r)
            {
                return Avx2.ShiftRightLogical(Avx2.Multiply(r.AsUInt32(), _cA.AsUInt32()), 37);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static Vector256<ulong> DIV3364(Vector256<ulong> r)
            {
                return Avx2.ShiftRightLogical(Avx2.Multiply(Avx2.ShiftRightLogical(r, 2).AsUInt32(), _cB.AsUInt32()), 40);
            }

            //<0, 1, 43, 39, 26, 27, 6, 25, 13, 17, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0>
            var div1 = Div58(div0);
            var rem0 = Avx2.Subtract(div0, Avx2.Multiply(div1.AsUInt32(), _58.AsUInt32()));

            var div2 = DIV3364(div0);
            var rem1 = Avx2.Subtract(div1, Avx2.Multiply(div2.AsUInt32(), _58.AsUInt32()));

            var div3 = DIV3364(div1);
            var rem2 = Avx2.Subtract(div2, Avx2.Multiply(div3.AsUInt32(), _58.AsUInt32()));

            var div4 = DIV3364(div2);
            var rem3 = Avx2.Subtract(div3, Avx2.Multiply(div4.AsUInt32(), _58.AsUInt32()));

            var rem4 = div4;

            /*  Okay, we have all 20 terms we need at this point, but they're
                spread out over 5 registers. Each value is stored as an 8B long,
                even though it's less than 58, so 7 of those bytes are 0.  That
                means we're only taking up 4 bytes in each register.  We need to
                get them to a more compact form, but the correct order (in terms of
                place value and recalling where the input vector comes from) is:
                (letters in the right column correspond to diagram below)

                the first value in rem4  (a)
                the first value in rem3  (b)
                ...
                the first value in rem0  (e)
                the second value in rem4 (f)
                ...
                the fourth value in rem0 (t)

                The fact that moves that cross the 128 bit boundary are tricky in
                AVX makes this difficult, forcing an inconvenient output format.

                First, we'll use _mm256_shuffle_epi8 to move the second value in
                each half to byte 5:

                [ a 0 0 0 0 0 0 0  f 0 0 0 0 0 0 0 | k 0 0 0 0 0 0 0  p 0 0 0 0 0 0 0 ] ->
                [ a 0 0 0 0 f 0 0  0 0 0 0 0 0 0 0 | k 0 0 0 0 p 0 0  0 0 0 0 0 0 0 0 ]

                Then for the vectors other than rem4, we'll shuffle them the same
                way, but then shift them left (which corresponds to right in the
                picture...) and OR them together.  */

            var shift4 = Avx2.Shuffle(rem4.AsByte(), _shuffle1);
            var shift3 = Avx2.ShiftLeftLogical128BitLane(Avx2.Shuffle(rem3.AsByte(), _shuffle1), 1);
            var shift2 = Avx2.ShiftLeftLogical128BitLane(Avx2.Shuffle(rem2.AsByte(), _shuffle1), 2);
            var shift1 = Avx2.ShiftLeftLogical128BitLane(Avx2.Shuffle(rem1.AsByte(), _shuffle1), 3);
            var shift0 = Avx2.ShiftLeftLogical128BitLane(Avx2.Shuffle(rem0.AsByte(), _shuffle1), 4);
            var shift = Avx2.Or(Avx2.Or(Avx2.Or(shift4, shift3), Avx2.Or(shift2, shift1)), shift0);

            return shift;
        }


        /* Converts each byte in the AVX2 register from raw base58 [0,58) to
           base58 digits ('1'-'z', with some skips).  Anything not in the range
           [0, 58) will be mapped arbitrarily, but won't affect other bytes. */
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<sbyte> RawToBase58(Vector256<sbyte> input)
        {
            /* <30 cycles for two vectors (64 conversions) */
            /* We'll perform the map as an arithmetic expression,
               b58ch(x) = '1' + x + 7*[x>8] + [x>16] + [x>21] + 6*[x>32] + [x>43]
               (using Knuth bracket notation, which maps true/false to 1/0).

               cmpgt uses 0xFF for true and 0x00 for false.  This is very
               convenient, because most of the time we just want to skip one
               character, so we can add 1 by subtracting 0xFF (=-1). */

            var gt0 = Avx2.CompareGreaterThan(input, Vector256.Create((sbyte)8)); /* skip 7 */
            var gt1 = Avx2.CompareGreaterThan(input, Vector256.Create((sbyte)16));
            var gt2 = Avx2.CompareGreaterThan(input, Vector256.Create((sbyte)21));
            var gt3 = Avx2.CompareGreaterThan(input, Vector256.Create((sbyte)32)); /* skip 6*/
            var gt4 = Avx2.CompareGreaterThan(input, Vector256.Create((sbyte)43));

            /* Intel doesn't give us an epi8 multiplication instruction, but since
               we know the input is all in {0, -1}, we can just AND both values
               with -7 to get {0, -7}. Similarly for 6. */

            var gt0_7 = Avx2.And(gt0, Vector256.Create((sbyte)-7));
            var gt3_6 = Avx2.And(gt3, Vector256.Create((sbyte)-6));

            /* Add up all the negative offsets. */
            var sum = Avx2.Add(
                            Avx2.Add(
                              Avx2.Add(Vector256.Create((sbyte)-'1'), gt1), /* Yes, that's the negative character value of '1' */
                              Avx2.Add(gt2, gt4)),
                            Avx2.Add(gt0_7, gt3_6));

            return Avx2.Subtract(input, sum);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CountLeadingZeros32(Vector256<byte> input)
        {
            var mask0 = ~Avx2.MoveMask(Avx2.CompareEqual(input, Vector256<byte>.Zero));

            return BitOperations.TrailingZeroCount(mask0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CountLeadingZeros45(Vector256<byte> input0, Vector256<byte> input1)
        {
            ulong mask0 = (ulong)~Avx2.MoveMask(Avx2.CompareEqual(input0, Vector256<byte>.Zero));
            ulong mask1 = (ulong)~Avx2.MoveMask(Avx2.CompareEqual(input1, Vector256<byte>.Zero));
            //1 << ((n & 63) - 1)
            var mask = ((((mask1 | masklb(14))) << 32) | mask0);

            return BitOperations.TrailingZeroCount(mask);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            ulong masklb(ulong r)
            {
                return 1ul << (int)((r & 63ul) - 1ul);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void TenPerSlotDown32(Vector256<byte> in0, Vector256<byte> in1, Vector256<byte> in2, out Vector256<byte> out0, out Vector256<byte> out1)
        {
            //var rightMask = Vector256.Create(0, 0, 0, 0, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF).AsByte();
            //var leftMask = Vector256.Create(0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0, 0, 0, 0).AsByte();

            //var a = Avx2.ShiftLeftLogical128BitLane(Avx2.PermuteVar8x32(in0.AsUInt32(), Vector256.Create(4, 5, 6, 7, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF)).AsByte(), 10);
            //var a2 = Avx2.And(Avx2.ShiftRightLogical128BitLane(in0, 6), rightMask);
            //var a3 = Avx2.Or(a, a2);
            //var a4 = Avx2.Or(Avx2.And(in0, leftMask.AsByte()), a3);

            //var b = Avx2.PermuteVar8x32(in1.AsUInt32(), Vector256.Create(4u, 5, 6, 7, 0, 1, 2, 4)).AsByte();
            //var bb = Avx2.Shuffle(b, Vector256.Create(255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 28, 29));

            //var c = Avx2.ShiftRightLogical128BitLane(Avx2.And(b, leftMask), 2);
            //var d = Avx2.ShiftLeftLogical128BitLane(in2, 8);

            //out0 = Avx2.Or(a4, bb);
            //out1 = Avx2.Or(c, d);

            //return;

            var lo0 = Avx.ExtractVector128(in0, 0);
            var hi0 = Avx.ExtractVector128(in0, 1);
            var lo1 = Avx.ExtractVector128(in1, 0);
            var hi1 = Avx.ExtractVector128(in1, 1);
            var lo2 = Avx.ExtractVector128(in2, 0);

            var o0 = Sse2.Or(lo0, Sse2.ShiftLeftLogical128BitLane(hi0, 10));
            var o1 = Sse2.Or(Sse2.Or(
                                            Sse2.ShiftRightLogical128BitLane(hi0, 6),
                                            Sse2.ShiftLeftLogical128BitLane(lo1, 4)
                                            ), Sse2.ShiftLeftLogical128BitLane(hi1, 14));
            var o2 = Sse2.Or(Sse2.ShiftRightLogical128BitLane(hi1, 2), Sse2.ShiftLeftLogical128BitLane(lo2, 8));

            out0 = Vector256.Create(o0, o1);
            out1 = Vector256.Create(o2, Vector128<byte>.Zero);
        }
    }
}
