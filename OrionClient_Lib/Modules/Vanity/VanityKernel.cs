using ILGPU;
using ILGPU.Backends.PTX;
using ILGPU.IR.Intrinsics;
using ILGPU.IR;
using ILGPU.Runtime;
using OrionClientLib.Hashers.GPU;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using ILGPU.Backends;

namespace OrionClientLib.Modules.Vanity
{
    internal struct VanityKernel
    {
        public static void Kernel(RunData data)
        {
            var grid = Grid.GlobalIndex;
            var group = Group.Dimension;

            int index = grid.X * group.Y + grid.Y;

            int location = index * 32;

            //Interop.WriteLine("{0}", location);

            SignKeyPair(index, data.PrivateBuffer.SubView(location, 32), data);
        }

        private static void SignKeyPair(int index, ArrayView<byte> privateKey, RunData data)
        {
            Sha512GPU sha512 = new Sha512GPU();
            sha512.Init();

            var h = sha512.Hash(privateKey);

            h[0] &= 0xf8;
            h[31] = (byte)((h[31] | 0x40) & 0x7f);

            Ext_POINT c = BasePointMult(h.Cast<uint>(), data.Folding_PA);

            uint b = (((c.Y.X7 >> 24) & 0x7f) | ((c.X.X0 & 1) << 7));

            c.Y.X7 &= 0x00FFFFFF;
            c.Y.X7 |= b << 24;

            data.PublicBuffer.Cast<UInt256>()[index] = c.Y;
            data.VanityBuffer.Cast<UInt256>()[index] = Base58Encode(c.Y);
        }

        #region SHA512

        public struct Sha512GPU
        {
            public ulong curlen;
            public ulong length;
            //public ulong[] state;
            //public ArrayView<byte> buf;
            //private State state;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Init()
            {
                //state = new State(0);
                curlen = 0;
                length = 0;
                //buf = LocalMemory.Allocate1D<byte>(128);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void Store64H(ulong x, ArrayView<ulong> y, int offset)
            {
                y[offset] = BinaryPrimitives.ReverseEndianness(x);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void Load64H(ref ulong x, ArrayView<byte> y, int offset)
            {
                x = y.SubView(offset, 8).Cast<ulong>()[0];
                x = BinaryPrimitives.ReverseEndianness(x);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void RND(ulong a, ulong b, ulong c, ref ulong d, ulong e, ulong f, ulong g, ref ulong h, ulong k, ulong w)
            {
                unchecked
                {
                    h += k + Sigma1(e) + Ch(e, f, g) + w;
                    d += h;
                    h += Sigma0(a) + Maj(a, b, c);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            ulong S(ulong x, int shift)
            {
                return (x >> shift) | (x << (64 - shift));

                //return BitOperations.RotateRight(x, shift);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            ulong Sigma0(ulong x)
            {
                return (S(x, 28) ^ S(x, 34) ^ S(x, 39));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            ulong Sigma1(ulong x)
            {
                return (S(x, 14) ^ S(x, 18) ^ S(x, 41));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            ulong Maj(ulong x, ulong y, ulong z)
            {
                return (((x | y) & z) | (x & y));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            ulong Ch(ulong x, ulong y, ulong z)
            {
                return (z ^ (x & (y ^ z)));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            ulong Gamma0(ulong x)
            {
                return (S(x, 1) ^ S(x, 8) ^ ((x) >> 7));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            ulong Gamma1(ulong x)
            {
                return (S(x, 19) ^ S(x, 61) ^ ((x) >> 6));
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            public ArrayView<byte> Hash(ArrayView<byte> bytes)
            {
                unchecked
                {
                    var buf = LocalMemory.Allocate<byte>(128);

                    ArrayView<ulong> ulongBuf = buf.Cast<ulong>();

                    for (int x = 0; x < 32; x += 2)
                    {
                        buf[x] = bytes[x];
                        buf[x + 1] = bytes[x + 1];
                    }

                    ulongBuf[4] = 0;
                    ulongBuf[5] = 0;
                    ulongBuf[6] = 0;
                    ulongBuf[7] = 0;
                    ulongBuf[8] = 0;
                    ulongBuf[9] = 0;
                    ulongBuf[10] = 0;
                    ulongBuf[11] = 0;
                    ulongBuf[12] = 0;
                    ulongBuf[13] = 0;
                    ulongBuf[14] = 0;
                    ulongBuf[15] = 0;

                    buf[32] = 0x80;
                    buf[126] = 1;

                    //State
                    ulong x0 = 0x6a09e667f3bcc908;
                    ulong x1 = 0xbb67ae8584caa73b;
                    ulong x2 = 0x3c6ef372fe94f82b;
                    ulong x3 = 0xa54ff53a5f1d36f1;
                    ulong x4 = 0x510e527fade682d1;
                    ulong x5 = 0x9b05688c2b3e6c1f;
                    ulong x6 = 0x1f83d9abfb41bd6b;
                    ulong x7 = 0x5be0cd19137e2179;

                    ArrayView<ulong> W = LocalMemory.Allocate<ulong>(80);

                    int i = 0;


                    W[i] = BinaryPrimitives.ReverseEndianness(ulongBuf[i]);
                    W[i + 1] = BinaryPrimitives.ReverseEndianness(ulongBuf[i + 1]);
                    W[i + 2] = BinaryPrimitives.ReverseEndianness(ulongBuf[i + 2]);
                    W[i + 3] = BinaryPrimitives.ReverseEndianness(ulongBuf[i + 3]);
                    W[i + 4] = BinaryPrimitives.ReverseEndianness(ulongBuf[i + 4]);
                    W[i + 5] = BinaryPrimitives.ReverseEndianness(ulongBuf[i + 5]);
                    W[i + 6] = BinaryPrimitives.ReverseEndianness(ulongBuf[i + 6]);
                    W[i + 7] = BinaryPrimitives.ReverseEndianness(ulongBuf[i + 7]);
                    W[i + 8] = BinaryPrimitives.ReverseEndianness(ulongBuf[i + 8]);
                    W[i + 9] = BinaryPrimitives.ReverseEndianness(ulongBuf[i + 9]);
                    W[i + 10] = BinaryPrimitives.ReverseEndianness(ulongBuf[i + 10]);
                    W[i + 11] = BinaryPrimitives.ReverseEndianness(ulongBuf[i + 11]);
                    W[i + 12] = BinaryPrimitives.ReverseEndianness(ulongBuf[i + 12]);
                    W[i + 13] = BinaryPrimitives.ReverseEndianness(ulongBuf[i + 13]);
                    W[i + 14] = BinaryPrimitives.ReverseEndianness(ulongBuf[i + 14]);
                    W[i + 15] = BinaryPrimitives.ReverseEndianness(ulongBuf[i + 15]);


                    for (i = 16; i < 80; i += 2)
                    {
                        W[i] = (Gamma1(W[i - 2]) + W[i - 7] + Gamma0(W[i - 15]) + W[i - 16]);
                        W[i + 1] = (Gamma1(W[i - 1]) + W[i - 6] + Gamma0(W[i - 14]) + W[i - 15]);
                    }

                    #region Steps

                    //0x428a2f98d728ae22,0x7137449123ef65cd,0xb5c0fbcfec4d3b2f,0xe9b5dba58189dbbc,
                    //0x3956c25bf348b538,0x59f111f1b605d019,0x923f82a4af194f9b,0xab1c5ed5da6d8118,
                    RND(x0, x1, x2, ref x3, x4, x5, x6, ref x7, 0x428a2f98d728ae22, W[0]);
                    RND(x7, x0, x1, ref x2, x3, x4, x5, ref x6, 0x7137449123ef65cd, W[1]);
                    RND(x6, x7, x0, ref x1, x2, x3, x4, ref x5, 0xb5c0fbcfec4d3b2f, W[2]);
                    RND(x5, x6, x7, ref x0, x1, x2, x3, ref x4, 0xe9b5dba58189dbbc, W[3]);
                    RND(x4, x5, x6, ref x7, x0, x1, x2, ref x3, 0x3956c25bf348b538, W[4]);
                    RND(x3, x4, x5, ref x6, x7, x0, x1, ref x2, 0x59f111f1b605d019, W[5]);
                    RND(x2, x3, x4, ref x5, x6, x7, x0, ref x1, 0x923f82a4af194f9b, W[6]);
                    RND(x1, x2, x3, ref x4, x5, x6, x7, ref x0, 0xab1c5ed5da6d8118, W[7]);


                    //0xd807aa98a3030242,0x12835b0145706fbe,0x243185be4ee4b28c,0x550c7dc3d5ffb4e2,
                    //0x72be5d74f27b896f,0x80deb1fe3b1696b1,0x9bdc06a725c71235,0xc19bf174cf692694,
                    RND(x0, x1, x2, ref x3, x4, x5, x6, ref x7, 0xd807aa98a3030242, W[8]);
                    RND(x7, x0, x1, ref x2, x3, x4, x5, ref x6, 0x12835b0145706fbe, W[9]);
                    RND(x6, x7, x0, ref x1, x2, x3, x4, ref x5, 0x243185be4ee4b28c, W[10]);
                    RND(x5, x6, x7, ref x0, x1, x2, x3, ref x4, 0x550c7dc3d5ffb4e2, W[11]);
                    RND(x4, x5, x6, ref x7, x0, x1, x2, ref x3, 0x72be5d74f27b896f, W[12]);
                    RND(x3, x4, x5, ref x6, x7, x0, x1, ref x2, 0x80deb1fe3b1696b1, W[13]);
                    RND(x2, x3, x4, ref x5, x6, x7, x0, ref x1, 0x9bdc06a725c71235, W[14]);
                    RND(x1, x2, x3, ref x4, x5, x6, x7, ref x0, 0xc19bf174cf692694, W[15]);


                    //0xe49b69c19ef14ad2,0xefbe4786384f25e3,0x0fc19dc68b8cd5b5,0x240ca1cc77ac9c65,
                    //0x2de92c6f592b0275,0x4a7484aa6ea6e483,0x5cb0a9dcbd41fbd4,0x76f988da831153b5,
                    RND(x0, x1, x2, ref x3, x4, x5, x6, ref x7, 0xe49b69c19ef14ad2, W[16]);
                    RND(x7, x0, x1, ref x2, x3, x4, x5, ref x6, 0xefbe4786384f25e3, W[17]);
                    RND(x6, x7, x0, ref x1, x2, x3, x4, ref x5, 0x0fc19dc68b8cd5b5, W[18]);
                    RND(x5, x6, x7, ref x0, x1, x2, x3, ref x4, 0x240ca1cc77ac9c65, W[19]);
                    RND(x4, x5, x6, ref x7, x0, x1, x2, ref x3, 0x2de92c6f592b0275, W[20]);
                    RND(x3, x4, x5, ref x6, x7, x0, x1, ref x2, 0x4a7484aa6ea6e483, W[21]);
                    RND(x2, x3, x4, ref x5, x6, x7, x0, ref x1, 0x5cb0a9dcbd41fbd4, W[22]);
                    RND(x1, x2, x3, ref x4, x5, x6, x7, ref x0, 0x76f988da831153b5, W[23]);



                    // 0x983e5152ee66dfab,0xa831c66d2db43210,0xb00327c898fb213f,0xbf597fc7beef0ee4,
                    //0xc6e00bf33da88fc2,0xd5a79147930aa725,0x06ca6351e003826f,0x142929670a0e6e70,
                    RND(x0, x1, x2, ref x3, x4, x5, x6, ref x7, 0x983e5152ee66dfab, W[24]);
                    RND(x7, x0, x1, ref x2, x3, x4, x5, ref x6, 0xa831c66d2db43210, W[25]);
                    RND(x6, x7, x0, ref x1, x2, x3, x4, ref x5, 0xb00327c898fb213f, W[26]);
                    RND(x5, x6, x7, ref x0, x1, x2, x3, ref x4, 0xbf597fc7beef0ee4, W[27]);
                    RND(x4, x5, x6, ref x7, x0, x1, x2, ref x3, 0xc6e00bf33da88fc2, W[28]);
                    RND(x3, x4, x5, ref x6, x7, x0, x1, ref x2, 0xd5a79147930aa725, W[29]);
                    RND(x2, x3, x4, ref x5, x6, x7, x0, ref x1, 0x06ca6351e003826f, W[30]);
                    RND(x1, x2, x3, ref x4, x5, x6, x7, ref x0, 0x142929670a0e6e70, W[31]);


                    //0x27b70a8546d22ffc,0x2e1b21385c26c926,0x4d2c6dfc5ac42aed,0x53380d139d95b3df,
                    //0x650a73548baf63de,0x766a0abb3c77b2a8,0x81c2c92e47edaee6,0x92722c851482353b,
                    RND(x0, x1, x2, ref x3, x4, x5, x6, ref x7, 0x27b70a8546d22ffc, W[32]);
                    RND(x7, x0, x1, ref x2, x3, x4, x5, ref x6, 0x2e1b21385c26c926, W[33]);
                    RND(x6, x7, x0, ref x1, x2, x3, x4, ref x5, 0x4d2c6dfc5ac42aed, W[34]);
                    RND(x5, x6, x7, ref x0, x1, x2, x3, ref x4, 0x53380d139d95b3df, W[35]);
                    RND(x4, x5, x6, ref x7, x0, x1, x2, ref x3, 0x650a73548baf63de, W[36]);
                    RND(x3, x4, x5, ref x6, x7, x0, x1, ref x2, 0x766a0abb3c77b2a8, W[37]);
                    RND(x2, x3, x4, ref x5, x6, x7, x0, ref x1, 0x81c2c92e47edaee6, W[38]);
                    RND(x1, x2, x3, ref x4, x5, x6, x7, ref x0, 0x92722c851482353b, W[39]);


                    //0xa2bfe8a14cf10364,0xa81a664bbc423001,0xc24b8b70d0f89791,0xc76c51a30654be30,
                    //0xd192e819d6ef5218,0xd69906245565a910,0xf40e35855771202a,0x106aa07032bbd1b8,
                    RND(x0, x1, x2, ref x3, x4, x5, x6, ref x7, 0xa2bfe8a14cf10364, W[40]);
                    RND(x7, x0, x1, ref x2, x3, x4, x5, ref x6, 0xa81a664bbc423001, W[41]);
                    RND(x6, x7, x0, ref x1, x2, x3, x4, ref x5, 0xc24b8b70d0f89791, W[42]);
                    RND(x5, x6, x7, ref x0, x1, x2, x3, ref x4, 0xc76c51a30654be30, W[43]);
                    RND(x4, x5, x6, ref x7, x0, x1, x2, ref x3, 0xd192e819d6ef5218, W[44]);
                    RND(x3, x4, x5, ref x6, x7, x0, x1, ref x2, 0xd69906245565a910, W[45]);
                    RND(x2, x3, x4, ref x5, x6, x7, x0, ref x1, 0xf40e35855771202a, W[46]);
                    RND(x1, x2, x3, ref x4, x5, x6, x7, ref x0, 0x106aa07032bbd1b8, W[47]);


                    //0x19a4c116b8d2d0c8,0x1e376c085141ab53,0x2748774cdf8eeb99,0x34b0bcb5e19b48a8,
                    //0x391c0cb3c5c95a63,0x4ed8aa4ae3418acb,0x5b9cca4f7763e373,0x682e6ff3d6b2b8a3,
                    RND(x0, x1, x2, ref x3, x4, x5, x6, ref x7, 0x19a4c116b8d2d0c8, W[48]);
                    RND(x7, x0, x1, ref x2, x3, x4, x5, ref x6, 0x1e376c085141ab53, W[49]);
                    RND(x6, x7, x0, ref x1, x2, x3, x4, ref x5, 0x2748774cdf8eeb99, W[50]);
                    RND(x5, x6, x7, ref x0, x1, x2, x3, ref x4, 0x34b0bcb5e19b48a8, W[51]);
                    RND(x4, x5, x6, ref x7, x0, x1, x2, ref x3, 0x391c0cb3c5c95a63, W[52]);
                    RND(x3, x4, x5, ref x6, x7, x0, x1, ref x2, 0x4ed8aa4ae3418acb, W[53]);
                    RND(x2, x3, x4, ref x5, x6, x7, x0, ref x1, 0x5b9cca4f7763e373, W[54]);
                    RND(x1, x2, x3, ref x4, x5, x6, x7, ref x0, 0x682e6ff3d6b2b8a3, W[55]);


                    //0x748f82ee5defb2fc,0x78a5636f43172f60,0x84c87814a1f0ab72,0x8cc702081a6439ec,
                    //0x90befffa23631e28,0xa4506cebde82bde9,0xbef9a3f7b2c67915,0xc67178f2e372532b,
                    RND(x0, x1, x2, ref x3, x4, x5, x6, ref x7, 0x748f82ee5defb2fc, W[56]);
                    RND(x7, x0, x1, ref x2, x3, x4, x5, ref x6, 0x78a5636f43172f60, W[57]);
                    RND(x6, x7, x0, ref x1, x2, x3, x4, ref x5, 0x84c87814a1f0ab72, W[58]);
                    RND(x5, x6, x7, ref x0, x1, x2, x3, ref x4, 0x8cc702081a6439ec, W[59]);
                    RND(x4, x5, x6, ref x7, x0, x1, x2, ref x3, 0x90befffa23631e28, W[60]);
                    RND(x3, x4, x5, ref x6, x7, x0, x1, ref x2, 0xa4506cebde82bde9, W[61]);
                    RND(x2, x3, x4, ref x5, x6, x7, x0, ref x1, 0xbef9a3f7b2c67915, W[62]);
                    RND(x1, x2, x3, ref x4, x5, x6, x7, ref x0, 0xc67178f2e372532b, W[63]);


                    //0xca273eceea26619c,0xd186b8c721c0c207,0xeada7dd6cde0eb1e,0xf57d4f7fee6ed178,
                    //0x06f067aa72176fba,0x0a637dc5a2c898a6,0x113f9804bef90dae,0x1b710b35131c471b,
                    RND(x0, x1, x2, ref x3, x4, x5, x6, ref x7, 0xca273eceea26619c, W[64]);
                    RND(x7, x0, x1, ref x2, x3, x4, x5, ref x6, 0xd186b8c721c0c207, W[65]);
                    RND(x6, x7, x0, ref x1, x2, x3, x4, ref x5, 0xeada7dd6cde0eb1e, W[66]);
                    RND(x5, x6, x7, ref x0, x1, x2, x3, ref x4, 0xf57d4f7fee6ed178, W[67]);
                    RND(x4, x5, x6, ref x7, x0, x1, x2, ref x3, 0x06f067aa72176fba, W[68]);
                    RND(x3, x4, x5, ref x6, x7, x0, x1, ref x2, 0x0a637dc5a2c898a6, W[69]);
                    RND(x2, x3, x4, ref x5, x6, x7, x0, ref x1, 0x113f9804bef90dae, W[70]);
                    RND(x1, x2, x3, ref x4, x5, x6, x7, ref x0, 0x1b710b35131c471b, W[71]);


                    //0x28db77f523047d84,0x32caab7b40c72493,0x3c9ebe0a15c9bebc,0x431d67c49c100d4c,
                    //0x4cc5d4becb3e42b6,0x597f299cfc657e2a,0x5fcb6fab3ad6faec,0x6c44198c4a475817
                    RND(x0, x1, x2, ref x3, x4, x5, x6, ref x7, 0x28db77f523047d84, W[72]);
                    RND(x7, x0, x1, ref x2, x3, x4, x5, ref x6, 0x32caab7b40c72493, W[73]);
                    RND(x6, x7, x0, ref x1, x2, x3, x4, ref x5, 0x3c9ebe0a15c9bebc, W[74]);
                    RND(x5, x6, x7, ref x0, x1, x2, x3, ref x4, 0x431d67c49c100d4c, W[75]);
                    RND(x4, x5, x6, ref x7, x0, x1, x2, ref x3, 0x4cc5d4becb3e42b6, W[76]);
                    RND(x3, x4, x5, ref x6, x7, x0, x1, ref x2, 0x597f299cfc657e2a, W[77]);
                    RND(x2, x3, x4, ref x5, x6, x7, x0, ref x1, 0x5fcb6fab3ad6faec, W[78]);
                    RND(x1, x2, x3, ref x4, x5, x6, x7, ref x0, 0x6c44198c4a475817, W[79]);

                    #endregion

                    Store64H(0x6a09e667f3bcc908 + x0, ulongBuf, 0);
                    Store64H(0xbb67ae8584caa73b + x1, ulongBuf, 1);
                    Store64H(0x3c6ef372fe94f82b + x2, ulongBuf, 2);
                    Store64H(0xa54ff53a5f1d36f1 + x3, ulongBuf, 3);
                    Store64H(0x510e527fade682d1 + x4, ulongBuf, 4);
                    Store64H(0x9b05688c2b3e6c1f + x5, ulongBuf, 5);
                    Store64H(0x1f83d9abfb41bd6b + x6, ulongBuf, 6);
                    Store64H(0x5be0cd19137e2179 + x7, ulongBuf, 7);

                    return buf;
                }
            }
        }

        #endregion

        #region Base58Encode

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetLeading0s(uint x0, uint x1, uint x2, uint x3, uint x4, uint x5, uint x6, uint x7, uint x8, uint x9, uint x10, uint x11)
        {
            int totalBits = IntrinsicMath.LeadingZeroCount(x0);
            if (totalBits == 32) { totalBits += IntrinsicMath.LeadingZeroCount(x1); }
            if (totalBits == 64) { totalBits += IntrinsicMath.LeadingZeroCount(x2); }
            if (totalBits == 96) { totalBits += IntrinsicMath.LeadingZeroCount(x3); }
            if (totalBits == 128) { totalBits += IntrinsicMath.LeadingZeroCount(x4); }
            if (totalBits == 160) { totalBits += IntrinsicMath.LeadingZeroCount(x5); }
            if (totalBits == 192) { totalBits += IntrinsicMath.LeadingZeroCount(x6); }
            if (totalBits == 224) { totalBits += IntrinsicMath.LeadingZeroCount(x7); }
            if (totalBits == 256) { totalBits += IntrinsicMath.LeadingZeroCount(x8); }
            if (totalBits == 288) { totalBits += IntrinsicMath.LeadingZeroCount(x9); }
            if (totalBits == 320) { totalBits += IntrinsicMath.LeadingZeroCount(x10); }
            if (totalBits == 352) { totalBits += IntrinsicMath.LeadingZeroCount(x11); }

            return totalBits >> 3;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetLeading0s(UInt256 x)
        {
            return GetLeading0s(x.X0, x.X1, x.X2, x.X3, x.X4, x.X5, x.X6, x.X7, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF);
        }

        [IntrinsicMethod(nameof(Mulu64hi_Generate))]
        [IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Mul64hi(ulong a, ulong b)
        {
            uint num2 = (uint)a;
            uint num3 = (uint)(a >> 32);
            uint num4 = (uint)b;
            uint num5 = (uint)(b >> 32);
            ulong num6 = (ulong)num2 * (ulong)num4;
            ulong num7 = (ulong)((long)num3 * (long)num4) + (num6 >> 32);
            ulong num8 = (ulong)((long)num2 * (long)num5 + (uint)num7);
            return (ulong)((long)num3 * (long)num5 + (long)(num7 >> 32)) + (num8 >> 32);
        }

        private static void Mulu64hi_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var a = codeGenerator.LoadPrimitive(value[0]);
            var b = codeGenerator.LoadPrimitive(value[1]);
            var returnValue = codeGenerator.AllocateHardware(value);

            var command = codeGenerator.BeginCommand($"mul.hi.u64");
            command.AppendArgument(returnValue);
            command.AppendArgument(a);
            command.AppendArgument(b);
            command.Dispose();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Div58_5(ref ulong c, ref ulong p)
        {
            var div = Mul64hi(c, 7544311872078572213UL) >> 28;
            var remainder = c - (div * 656356768UL);

            p += div;
            c = remainder;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Div58(ulong r)
        {
            return (uint)((r * 2369637129ul) >> 37);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Div3364(ulong r)
        {
            return (uint)(((r >> 2) * 1307386003ul) >> 40);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static UInt256 Base58Encode(UInt256 x)
        {

            Warp.Barrier();

            x.X0 = BinaryPrimitives.ReverseEndianness(x.X0);
            x.X1 = BinaryPrimitives.ReverseEndianness(x.X1);
            x.X2 = BinaryPrimitives.ReverseEndianness(x.X2);
            x.X3 = BinaryPrimitives.ReverseEndianness(x.X3);
            x.X4 = BinaryPrimitives.ReverseEndianness(x.X4);
            x.X5 = BinaryPrimitives.ReverseEndianness(x.X5);
            x.X6 = BinaryPrimitives.ReverseEndianness(x.X6);
            x.X7 = BinaryPrimitives.ReverseEndianness(x.X7);

            int inLeading0s = GetLeading0s(x);

            ulong x0 = 0;
            ulong x1 = x.X0 * 513735UL;
            ulong x2 = x.X0 * 77223048UL + x.X1 * 78508UL;
            ulong x3 = x.X0 * 437087610UL + x.X1 * 646269101UL + x.X2 * 11997UL;
            ulong x4 = x.X0 * 300156666UL + x.X1 * 118408823UL + x.X2 * 486083817UL + x.X3 * 1833UL;

            ulong x5 = x.X0 * 605448490UL + x.X1 * 91512303UL + x.X2 * 3737691UL + x.X3 * 324463681UL + x.X4 * 280UL;
            ulong x6 = x.X0 * 214625350UL + x.X1 * 209184527UL + x.X2 * 294005210UL + x.X3 * 385795061UL + x.X4 * 127692781UL + x.X5 * 42UL;
            ulong x7 = x.X0 * 141436834UL + x.X1 * 413102373UL + x.X2 * 247894721UL + x.X3 * 551597588UL + x.X4 * 389432875UL + x.X5 * 537767569UL + x.X6 * 6UL;
            ulong x8 = x.X0 * 379377856UL + x.X1 * 153715680UL + x.X2 * 289024608UL + x.X3 * 21339008UL + x.X4 * 357132832UL + x.X5 * 410450016UL + x.X6 * 356826688UL + x.X7;

            Div58_5(ref x8, ref x7);
            Div58_5(ref x7, ref x6);
            Div58_5(ref x6, ref x5);
            Div58_5(ref x5, ref x4);
            Div58_5(ref x4, ref x3);
            Div58_5(ref x3, ref x2);
            Div58_5(ref x2, ref x1);
            Div58_5(ref x1, ref x0);


            uint t0 = 0;
            uint t1 = 0;
            uint t2 = 0;
            uint t3 = 0;
            uint t4 = 0;
            uint t5 = 0;
            uint t6 = 0;
            uint t7 = 0;
            uint t8 = 0;
            uint t9 = 0;
            uint t10 = 0;
            uint t11 = 0;

            #region X0

            uint div1 = Div58(x0);
            t1 |= (uint)((x0 - div1 * 58));

            var div2 = Div3364(x0);
            t0 |= (uint)((div1 - div2 * 58)) << 24;

            var div3 = Div3364(div1);
            t0 |= (uint)((div2 - div3 * 58)) << 16;

            var div4 = Div3364(div2);
            t0 |= (uint)((div3 - div4 * 58)) << 8;

            t0 |= div4;

            #endregion

            #region X1

            div1 = Div58(x1);
            t2 |= (uint)((x1 - div1 * 58)) << 8;

            div2 = Div3364(x1);
            t2 |= (uint)((div1 - div2 * 58)) << 0;

            div3 = Div3364(div1);
            t1 |= (uint)((div2 - div3 * 58)) << 24;

            div4 = Div3364(div2);
            t1 |= (uint)((div3 - div4 * 58)) << 16;

            t1 |= div4 << 8;

            #endregion

            #region X2

            div1 = Div58(x2);
            t3 |= (uint)((x2 - div1 * 58)) << 16;

            div2 = Div3364(x2);
            t3 |= (uint)((div1 - div2 * 58)) << 8;

            div3 = Div3364(div1);
            t3 |= (uint)((div2 - div3 * 58)) << 0;

            div4 = Div3364(div2);
            t2 |= (uint)((div3 - div4 * 58)) << 24;

            t2 |= div4 << 16;

            #endregion

            #region X3

            div1 = Div58(x3);
            t4 |= (uint)((x3 - div1 * 58)) << 24;

            div2 = Div3364(x3);
            t4 |= (uint)((div1 - div2 * 58)) << 16;

            div3 = Div3364(div1);
            t4 |= (uint)((div2 - div3 * 58)) << 8;

            div4 = Div3364(div2);
            t4 |= (uint)((div3 - div4 * 58)) << 0;

            t3 |= div4 << 24;

            #endregion

            #region X4

            div1 = Div58(x4);
            t6 |= (uint)((x4 - div1 * 58));

            div2 = Div3364(x4);
            t5 |= (uint)((div1 - div2 * 58)) << 24;

            div3 = Div3364(div1);
            t5 |= (uint)((div2 - div3 * 58)) << 16;

            div4 = Div3364(div2);
            t5 |= (uint)((div3 - div4 * 58)) << 8;

            t5 |= div4;

            #endregion

            #region X5

            div1 = Div58(x5);
            t7 |= (uint)((x5 - div1 * 58)) << 8;

            div2 = Div3364(x5);
            t7 |= (uint)((div1 - div2 * 58)) << 0;

            div3 = Div3364(div1);
            t6 |= (uint)((div2 - div3 * 58)) << 24;

            div4 = Div3364(div2);
            t6 |= (uint)((div3 - div4 * 58)) << 16;

            t6 |= div4 << 8;

            #endregion

            #region X6

            div1 = Div58(x6);
            t8 |= (uint)((x6 - div1 * 58)) << 16;

            div2 = Div3364(x6);
            t8 |= (uint)((div1 - div2 * 58)) << 8;

            div3 = Div3364(div1);
            t8 |= (uint)((div2 - div3 * 58)) << 0;

            div4 = Div3364(div2);
            t7 |= (uint)((div3 - div4 * 58)) << 24;

            t7 |= div4 << 16;

            #endregion

            #region X7

            div1 = Div58(x7);
            t9 |= (uint)((x7 - div1 * 58)) << 24;

            div2 = Div3364(x7);
            t9 |= (uint)((div1 - div2 * 58)) << 16;

            div3 = Div3364(div1);
            t9 |= (uint)((div2 - div3 * 58)) << 8;

            div4 = Div3364(div2);
            t9 |= (uint)((div3 - div4 * 58)) << 0;

            t8 |= div4 << 24;

            #endregion

            #region X8

            div1 = Div58(x8);
            t11 |= (uint)((x8 - div1 * 58));

            div2 = Div3364(x8);
            t10 |= (uint)((div1 - div2 * 58)) << 24;

            div3 = Div3364(div1);
            t10 |= (uint)((div2 - div3 * 58)) << 16;

            div4 = Div3364(div2);
            t10 |= (uint)((div3 - div4 * 58)) << 8;

            t10 |= div4;

            #endregion

            var skip = GetLeading0s(
                BinaryPrimitives.ReverseEndianness(t0), BinaryPrimitives.ReverseEndianness(t1),
                BinaryPrimitives.ReverseEndianness(t2), BinaryPrimitives.ReverseEndianness(t3),
                BinaryPrimitives.ReverseEndianness(t4), BinaryPrimitives.ReverseEndianness(t5),
                BinaryPrimitives.ReverseEndianness(t6), BinaryPrimitives.ReverseEndianness(t7),
                BinaryPrimitives.ReverseEndianness(t8), BinaryPrimitives.ReverseEndianness(t9),
                BinaryPrimitives.ReverseEndianness(t10), BinaryPrimitives.ReverseEndianness(t11)) - inLeading0s;

            //Move entire sections
            while (skip >= 4)
            {
                t0 = t1;
                t1 = t2;
                t2 = t3;
                t3 = t4;
                t4 = t5;
                t5 = t6;
                t6 = t7;
                t7 = t8;
                t8 = t9;
                t9 = t10;
                t10 = t11;

                skip -= 4;
            }

            //Shift remaining bytes
            int bitMove = skip * 8;

            t0 = RotateRight(t0, t1, bitMove);
            t1 = RotateRight(t1, t2, bitMove);
            t2 = RotateRight(t2, t3, bitMove);
            t3 = RotateRight(t3, t4, bitMove);
            t4 = RotateRight(t4, t5, bitMove);
            t5 = RotateRight(t5, t6, bitMove);
            t6 = RotateRight(t6, t7, bitMove);
            t7 = RotateRight(t7, t8, bitMove);

            Warp.Barrier();

            //Only first 32 bytes will be returned. It's enough to get a prefix
            return new UInt256
            {
                X0 = t0,
                X1 = t1,
                X2 = t2,
                X3 = t3,
                X4 = t4,
                X5 = t5,
                X6 = t6,
                X7 = t7,
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint RotateRight(uint x0, uint x1, int shift)
        {
            //Can be changed to a funnel shift
            return (x0 >> shift) | (x1 << (32 - shift));
        }

        #endregion

        #region ED25519

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Ext_POINT BasePointMult(ArrayView<uint> sk, ArrayView<PA_POINT> folding)
        {
            UInt256 t = sk.Cast<UInt256>()[0];

            ArrayView<byte> cut = LocalMemory.Allocate<byte>(32);

            ecp_8Folds(t, cut);

            var p0 = folding[(int)cut[0]];

            Ext_POINT S = new Ext_POINT();

            S.X = SubReduce(S.X, p0.YpX, p0.YmX);
            S.Y = AddReduce(S.Y, p0.YpX, p0.YmX);

            var tWdi = new UInt256
            {
                X0 = 0xCDC9F843,
                X1 = 0x25E0F276,
                X2 = 0x4279542E,
                X3 = 0x0B5DD698,
                X4 = 0xCDB9CF66,
                X5 = 0x2B162114,
                X6 = 0x14D5CE43,
                X7 = 0x40907ED2,
            };


            S.T = MulReduce(S.T, p0.T2d, tWdi);

            //Set value
            S.Z.X0 = 2;

            for (int i = 1; i < 32; i++)
            {
                S = DoublePoint(S);
                S = AddAffinePoint(S, folding[(int)cut[i]]);
            }

            S.Z = Inverse(S.Z, S.Z);
            S.X = MulMod(S.X, S.X, S.Z);
            S.Y = MulMod(S.Y, S.Y, S.Z);

            return S;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Ext_POINT DoublePoint(Ext_POINT p)
        {
            UInt256 a = new UInt256();
            UInt256 b = new UInt256();
            UInt256 c = new UInt256();
            UInt256 d = new UInt256();
            UInt256 e = new UInt256();

            a = MulReduce(a, p.X, p.X);
            b = MulReduce(b, p.Y, p.Y);
            c = MulReduce(c, p.Z, p.Z);

            c = AddReduce(c, c, c);
            d = MaxPSubReduce(d, a);
            a = SubReduce(a, d, b);
            d = AddReduce(d, d, b);
            b = SubReduce(b, d, c);
            e = AddReduce(e, p.X, p.Y);
            e = MulReduce(e, e, e);
            e = AddReduce(e, e, a);

            p.X = MulReduce(p.X, e, b);
            p.Y = MulReduce(p.Y, a, d);
            p.Z = MulReduce(p.Z, d, b);
            p.T = MulReduce(p.T, e, a);

            return p;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Ext_POINT AddAffinePoint(Ext_POINT p, PA_POINT q)
        {
            UInt256 a = new UInt256();
            UInt256 b = new UInt256();
            UInt256 c = new UInt256();
            UInt256 d = new UInt256();
            UInt256 e = new UInt256();

            a = SubReduce(a, p.Y, p.X);
            a = MulReduce(a, a, q.YmX);

            b = AddReduce(b, p.Y, p.X);
            b = MulReduce(b, b, q.YpX);
            c = MulReduce(c, p.T, q.T2d);
            d = AddReduce(d, p.Z, p.Z);
            e = SubReduce(e, b, a);
            b = AddReduce(b, b, a);
            a = SubReduce(a, d, c);
            d = AddReduce(d, d, c);


            p.X = MulReduce(p.X, e, a);
            p.Y = MulReduce(p.Y, b, d);
            p.T = MulReduce(p.T, e, b);
            p.Z = MulReduce(p.Z, d, a);

            return p;
        }

        #region Mod

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static UInt256 MulMod(UInt256 Z, UInt256 X, UInt256 Y)
        {
            Z = MulReduce(Z, X, Y);
            Z = Mod(Z);

            return Z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static UInt256 Mod(UInt256 X)
        {
            UInt256 T;
            var subResult = SubMod(X, X);
            X = subResult.Item1;
            uint c = subResult.Item2;
            /* set T = 0 if c=0, else T = P */

            T.X0 = (c & 0xFFFFFFED);
            T.X1 = T.X2 = T.X3 = T.X4 = T.X5 = T.X6 = c;
            T.X7 = c >> 1;

            X = Add(X, X, T).Item1;   /* X += 0 or P */

            /* In case there is another P there */

            var result = SubMod(X, X);

            X = result.Item1;
            c = result.Item2;
            /* set T = 0 if c=0, else T = P */

            T.X0 = (c & 0xFFFFFFED);
            T.X1 = T.X2 = T.X3 = T.X4 = T.X5 = T.X6 = c;
            T.X7 = c >> 1;

            return Add(X, X, T).Item1;   /* X += 0 or P */
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (UInt256, uint) SubMod(UInt256 Z, UInt256 X)
        {
            unchecked
            {
                ulong c = 0;

                c = Sub32(X.X0, 0xFFFFFFED);
                Z.X0 = (uint)c;

                c = Sbc32(c, X.X1, 0xFFFFFFFF);
                Z.X1 = (uint)c;
                c = Sbc32(c, X.X2, 0xFFFFFFFF);
                Z.X2 = (uint)c;
                c = Sbc32(c, X.X3, 0xFFFFFFFF);
                Z.X3 = (uint)c;
                c = Sbc32(c, X.X4, 0xFFFFFFFF);
                Z.X4 = (uint)c;
                c = Sbc32(c, X.X5, 0xFFFFFFFF);
                Z.X5 = (uint)c;
                c = Sbc32(c, X.X6, 0xFFFFFFFF);
                Z.X6 = (uint)c;
                c = Sbc32(c, X.X7, 0x7FFFFFFF);
                Z.X7 = (uint)c;

                return (Z, (uint)(c >> 32));
            }
        }

        #endregion

        #region Inverse

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static UInt256 Inverse(UInt256 X, UInt256 z)
        {
            int i;

            UInt256 t0 = default, t1 = default, z2 = default, z9 = default, z11 = default;
            UInt256 z2_5_0 = default, z2_10_0 = default, z2_20_0 = default, z2_50_0 = default, z2_100_0 = default;

            /* 2 */
            z2 = MulReduce(z2, z, z);
            /* 4 */
            t1 = MulReduce(t1, z2, z2);
            /* 8 */
            t0 = SqrReduce(t1);
            /* 9 */
            z9 = MulReduce(z9, t0, z);
            /* 11 */
            z11 = MulReduce(z11, z9, z2);
            /* 22 */
            t0 = MulReduce(t0, z11, z11);
            /* 2^5 - 2^0 = 31 */
            z2_5_0 = MulReduce(z2_5_0, t0, z9);


            /* 2^6 - 2^1 */
            t0 = MulReduce(t0, z2_5_0, z2_5_0);
            /* 2^7 - 2^2 */
            t1 = SqrReduce(t0);
            /* 2^8 - 2^3 */
            t0 = SqrReduce(t1);
            /* 2^9 - 2^4 */
            t1 = SqrReduce(t0);
            /* 2^10 - 2^5 */
            t0 = SqrReduce(t1);
            /* 2^10 - 2^0 */
            z2_10_0 = MulReduce(z2_10_0, t0, z2_5_0);


            /* 2^11 - 2^1 */
            t0 = SqrReduce(z2_10_0);
            /* 2^12 - 2^2 */
            t1 = SqrReduce(t0);
            /* 2^20 - 2^10 */

            t0 = MulReduce(t0, t1, t1);
            t1 = MulReduce(t1, t0, t0);
            t0 = MulReduce(t0, t1, t1);
            t1 = MulReduce(t1, t0, t0);
            t0 = MulReduce(t0, t1, t1);
            t1 = MulReduce(t1, t0, t0);
            t0 = MulReduce(t0, t1, t1);
            t1 = MulReduce(t1, t0, t0);

            /* 2^20 - 2^0 */
            z2_20_0 = MulReduce(z2_20_0, t1, z2_10_0);

            /* 2^21 - 2^1 */
            t0 = SqrReduce(z2_20_0);
            /* 2^22 - 2^2 */
            t1 = SqrReduce(t0);

            /* 2^40 - 2^20 */

            (t0, t1) = Inverse_20(t0, t1);
            /* 2^40 - 2^0 */
            t0 = MulReduce(t0, t1, z2_20_0);

            /* 2^41 - 2^1 */
            t1 = SqrReduce(t0);
            /* 2^42 - 2^2 */
            t0 = SqrReduce(t1);
            /* 2^50 - 2^10 */


            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            /* 2^50 - 2^0 */
            z2_50_0 = MulReduce(z2_50_0, t0, z2_10_0);

            /* 2^51 - 2^1 */
            t0 = SqrReduce(z2_50_0);
            /* 2^52 - 2^2 */
            t1 = SqrReduce(t0);
            /* 2^100 - 2^50 */

            (t0, t1) = Inverse_50(t0, t1);

            /* 2^100 - 2^0 */
            z2_100_0 = MulReduce(z2_100_0, t1, z2_50_0);

            /* 2^101 - 2^1 */
            t1 = SqrReduce(z2_100_0);
            /* 2^102 - 2^2 */
            t0 = SqrReduce(t1);
            /* 2^200 - 2^100 */

            (t0, t1) = Inverse_100(t0, t1);

            /* 2^200 - 2^0 */
            t1 = MulReduce(t1, t0, z2_100_0);

            /* 2^201 - 2^1 */
            t0 = MulReduce(t0, t1, t1);
            /* 2^202 - 2^2 */
            t1 = MulReduce(t1, t0, t0);
            /* 2^250 - 2^50 */


            (t0, t1) = Inverse_50(t0, t1);
            /* 2^250 - 2^0 */
            t0 = MulReduce(t0, t1, z2_50_0);

            /* 2^251 - 2^1 */
            t1 = SqrReduce(t0);
            /* 2^252 - 2^2 */
            t0 = SqrReduce(t1);
            /* 2^253 - 2^3 */
            t1 = SqrReduce(t0);
            /* 2^254 - 2^4 */
            t0 = SqrReduce(t1);
            /* 2^255 - 2^5 */
            t1 = SqrReduce(t0);
            /* 2^255 - 21 */
            X = MulReduce(X, t1, z11);
            return X;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (UInt256 t0, UInt256 t1) Inverse_20(UInt256 t0, UInt256 t1)
        {
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);

            return (t0, t1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (UInt256 t0, UInt256 t1) Inverse_50(UInt256 t0, UInt256 t1)
        {
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);

            return (t0, t1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (UInt256 t0, UInt256 t1) Inverse_100(UInt256 t0, UInt256 t1)
        {
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);
            t1 = SqrReduce(t0);
            t0 = SqrReduce(t1);

            return (t0, t1);
        }

        #endregion

        #region Sqr

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static UInt256 SqrReduce(UInt256 X)
        {
            return MulReduce(X, X, X);
            //ulong c = 0;

            //U32_16 T = new U32_16();

            //T = MulSet(ref T, X.X0, X);

            //T = MulAdd1(T, X.X1, X);
            //T = MulAdd2(T, X.X2, X);
            //T = MulAdd3(T, X.X3, X);
            //T = MulAdd4(T, X.X4, X);
            //T = MulAdd5(T, X.X5, X);
            //T = MulAdd6(T, X.X6, X);
            //T = MulAdd7(T, X.X7, X);


            //Y = MulAddReduce(c, Y, T.X0, 38, T.X1);

            //return Y;
        }

        #endregion

        #region Multiply

        //Set inlining

        [IntrinsicMethod(nameof(MulReduce_Generate))]
        [IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static UInt256 MulReduce(UInt256 Z, UInt256 X, UInt256 Y)
        {
            //Reduce here

            ulong c = 0;

            U32_16 T = new U32_16();


            T = MulSet(ref T, X.X0, Y);

            T = MulAdd1(T, X.X1, Y);
            T = MulAdd2(T, X.X2, Y);
            T = MulAdd3(T, X.X3, Y);
            T = MulAdd4(T, X.X4, Y);
            T = MulAdd5(T, X.X5, Y);
            T = MulAdd6(T, X.X6, Y);
            T = MulAdd7(T, X.X7, Y);

            Z = MulAddReduce(c, Z, T.X0, 38, T.X1);

            return Z;
        }

        private static void MulReduce_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var z = (RegisterAllocator<PTXRegisterKind>.CompoundRegister)codeGenerator.Allocate(value);
            var x = (RegisterAllocator<PTXRegisterKind>.CompoundRegister)codeGenerator.Load(value[1]);
            var y = (RegisterAllocator<PTXRegisterKind>.CompoundRegister)codeGenerator.Load(value[2]);

            codeGenerator.Builder.AppendLine("{");

            codeGenerator.BeginCommand($".reg .u32 r0, r1, r2, r3, r4, r5, r6, r7, r8, r9, r10, r11, r12, r13, r14, r15, temp").Dispose();
            codeGenerator.BeginCommand($".reg .u64 carry").Dispose();

            codeGenerator.Builder.AppendLine($@"
    // Initialize result registers to zero
    mov.u32 r0, 0;  mov.u32 r1, 0;  mov.u32 r2, 0;  mov.u32 r3, 0;
    mov.u32 r4, 0;  mov.u32 r5, 0;  mov.u32 r6, 0;  mov.u32 r7, 0;
    mov.u32 r8, 0;  mov.u32 r9, 0;  mov.u32 r10, 0; mov.u32 r11, 0;
    mov.u32 r12, 0; mov.u32 r13, 0; mov.u32 r14, 0; mov.u32 r15, 0;

");

            #region MulSet

            for (int j = 0; j < 8; j++)
            {
                codeGenerator.BeginCommand($"mov.u64 carry, 0").Dispose();

                for (int i = 0; i < 8; i++)
                {
                    codeGenerator.BeginCommand($"mad.wide.u32 carry, {GetStringRepresentation(x.Children[j])}, {GetStringRepresentation(y.Children[i])}, carry").Dispose();
                    codeGenerator.BeginCommand($"mad.wide.u32 carry, r{i + j}, 1, carry").Dispose(); //Add previous value
                    codeGenerator.BeginCommand($"cvt.u32.u64 r{i + j}, carry").Dispose();
                    codeGenerator.BeginCommand($"shr.u64 carry, carry, 32").Dispose();
                }

                codeGenerator.BeginCommand($"cvt.u32.u64 r{j + 8}, carry").Dispose();
            }

            #endregion

            #region Mul Reduce

            codeGenerator.BeginCommand($"mov.u64 carry, 0").Dispose();

            for (int i = 0; i < 8; i++)
            {
                codeGenerator.BeginCommand($"mad.wide.u32 carry, r{i + 8}, 38, carry").Dispose();
                codeGenerator.BeginCommand($"mad.wide.u32 carry, r{i}, 1, carry").Dispose(); //Add previous value
                codeGenerator.BeginCommand($"cvt.u32.u64 {GetStringRepresentation(z.Children[i])}, carry").Dispose();
                codeGenerator.BeginCommand($"shr.u64 carry, carry, 32").Dispose();
            }

            codeGenerator.BeginCommand($"cvt.u32.u64 temp, carry").Dispose();

            for (int i = 0; i < 2; i++)
            {
                codeGenerator.BeginCommand($"mad.lo.cc.u32 {GetStringRepresentation(z.Children[0])}, temp, 38, {GetStringRepresentation(z.Children[0])}").Dispose();
                codeGenerator.BeginCommand($"madc.hi.cc.u32 temp, temp, 38, 0").Dispose();

                codeGenerator.BeginCommand($"addc.cc.u32 {GetStringRepresentation(z.Children[1])}, {GetStringRepresentation(z.Children[1])}, temp").Dispose();
                codeGenerator.BeginCommand($"addc.cc.u32 {GetStringRepresentation(z.Children[2])}, {GetStringRepresentation(z.Children[2])}, 0").Dispose();
                codeGenerator.BeginCommand($"addc.cc.u32 {GetStringRepresentation(z.Children[3])}, {GetStringRepresentation(z.Children[3])}, 0").Dispose();
                codeGenerator.BeginCommand($"addc.cc.u32 {GetStringRepresentation(z.Children[4])}, {GetStringRepresentation(z.Children[4])}, 0").Dispose();
                codeGenerator.BeginCommand($"addc.cc.u32 {GetStringRepresentation(z.Children[5])}, {GetStringRepresentation(z.Children[5])}, 0").Dispose();
                codeGenerator.BeginCommand($"addc.cc.u32 {GetStringRepresentation(z.Children[6])}, {GetStringRepresentation(z.Children[6])}, 0").Dispose();
                codeGenerator.BeginCommand($"addc.cc.u32 {GetStringRepresentation(z.Children[7])}, {GetStringRepresentation(z.Children[7])}, 0").Dispose();
                codeGenerator.BeginCommand($"addc.u32 temp, 0, 0").Dispose();

            }

            #endregion


            codeGenerator.Builder.AppendLine("}");

            var test = codeGenerator.Builder.ToString();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MulSetW0(uint b, uint X)
        {
            //c.u64 = (U64)(b)*(X); Y = c.u32.lo;

            unchecked
            {
                return (ulong)b * X;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MulSetW1(ulong c, uint b, uint X)
        {
            //c.u64 = (U64)(b) * (X) + c.u32.hi; Y = c.u32.lo;
            //TODO: Mad
            unchecked
            {
                return (ulong)b * X + (c >> 32);
                //return (uint)c;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static U32_16 MulSet(ref U32_16 Y, uint b, UInt256 X)
        {
            ulong c;

            unchecked
            {
                c = MulSetW0(b, X.X0);
                Y.X0.X0 = (uint)c;

                //7
                c = MulSetW1(c, b, X.X1);
                Y.X0.X1 = (uint)c;
                c = MulSetW1(c, b, X.X2);
                Y.X0.X2 = (uint)c;
                c = MulSetW1(c, b, X.X3);
                Y.X0.X3 = (uint)c;
                c = MulSetW1(c, b, X.X4);
                Y.X0.X4 = (uint)c;
                c = MulSetW1(c, b, X.X5);
                Y.X0.X5 = (uint)c;
                c = MulSetW1(c, b, X.X6);
                Y.X0.X6 = (uint)c;
                c = MulSetW1(c, b, X.X7);
                Y.X0.X7 = (uint)c;


                Y.X1.X0 = (uint)(c >> 32);
            }

            return Y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MulAddW0(uint Y, ulong b, uint X)
        {
            //c.u64 = (U64)(b)*(X) + (Y); Z = c.u32.lo;

            unchecked
            {
                return b * X + Y;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MulAddW1(ulong c, uint Y, ulong b, uint X)
        {
            //c.u64 = (U64)(b)*(X) + (U64)(Y) + c.u32.hi; Z = c.u32.lo;

            //TODO: MAD
            unchecked
            {
                return b * X + Y + (c >> 32);
            }
        }

        #region MulAdd

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static U32_16 MulAdd1(U32_16 Y, uint b, UInt256 X)
        {
            ulong c;

            #region Hate doing this (prevents local memory allocation somehow?)

            ulong bb = b;

            c = MulAddW0(Y.X0.X1, bb, X.X0);
            Y.X0.X1 = (uint)c;

            c = MulAddW1(c, Y.X0.X2, bb, X.X1);
            Y.X0.X2 = (uint)c;
            c = MulAddW1(c, Y.X0.X3, bb, X.X2);
            Y.X0.X3 = (uint)c;
            c = MulAddW1(c, Y.X0.X4, bb, X.X3);
            Y.X0.X4 = (uint)c;
            c = MulAddW1(c, Y.X0.X5, bb, X.X4);
            Y.X0.X5 = (uint)c;
            c = MulAddW1(c, Y.X0.X6, bb, X.X5);
            Y.X0.X6 = (uint)c;
            c = MulAddW1(c, Y.X0.X7, bb, X.X6);
            Y.X0.X7 = (uint)c;
            c = MulAddW1(c, Y.X1.X0, bb, X.X7);
            Y.X1.X0 = (uint)c;

            Y.X1.X1 = (uint)(c >> 32);

            return Y;

            #endregion
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static U32_16 MulAdd2(U32_16 Y, uint b, UInt256 X)
        {
            ulong c;

            #region Hate doing this (prevents local memory allocation somehow?)

            ulong bb = b;

            c = MulAddW0(Y.X0.X2, bb, X.X0);
            Y.X0.X2 = (uint)c;

            c = MulAddW1(c, Y.X0.X3, bb, X.X1);
            Y.X0.X3 = (uint)c;
            c = MulAddW1(c, Y.X0.X4, bb, X.X2);
            Y.X0.X4 = (uint)c;
            c = MulAddW1(c, Y.X0.X5, bb, X.X3);
            Y.X0.X5 = (uint)c;
            c = MulAddW1(c, Y.X0.X6, bb, X.X4);
            Y.X0.X6 = (uint)c;
            c = MulAddW1(c, Y.X0.X7, bb, X.X5);
            Y.X0.X7 = (uint)c;
            c = MulAddW1(c, Y.X1.X0, bb, X.X6);
            Y.X1.X0 = (uint)c;
            c = MulAddW1(c, Y.X1.X1, bb, X.X7);
            Y.X1.X1 = (uint)c;

            Y.X1.X2 = (uint)(c >> 32);

            return Y;

            #endregion
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static U32_16 MulAdd3(U32_16 Y, uint b, UInt256 X)
        {
            ulong c;

            #region Hate doing this (prevents local memory allocation somehow?)

            ulong bb = b;

            c = MulAddW0(Y.X0.X3, bb, X.X0);
            Y.X0.X3 = (uint)c;

            c = MulAddW1(c, Y.X0.X4, bb, X.X1);
            Y.X0.X4 = (uint)c;

            c = MulAddW1(c, Y.X0.X5, bb, X.X2);
            Y.X0.X5 = (uint)c;
            c = MulAddW1(c, Y.X0.X6, bb, X.X3);
            Y.X0.X6 = (uint)c;
            c = MulAddW1(c, Y.X0.X7, bb, X.X4);
            Y.X0.X7 = (uint)c;
            c = MulAddW1(c, Y.X1.X0, bb, X.X5);
            Y.X1.X0 = (uint)c;
            c = MulAddW1(c, Y.X1.X1, bb, X.X6);
            Y.X1.X1 = (uint)c;
            c = MulAddW1(c, Y.X1.X2, bb, X.X7);
            Y.X1.X2 = (uint)c;

            Y.X1.X3 = (uint)(c >> 32);

            return Y;

            #endregion
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static U32_16 MulAdd4(U32_16 Y, uint b, UInt256 X)
        {
            ulong c;

            #region Hate doing this (prevents local memory allocation somehow?)

            ulong bb = b;

            c = MulAddW0(Y.X0.X4, bb, X.X0);
            Y.X0.X4 = (uint)c;

            c = MulAddW1(c, Y.X0.X5, bb, X.X1);
            Y.X0.X5 = (uint)c;
            c = MulAddW1(c, Y.X0.X6, bb, X.X2);
            Y.X0.X6 = (uint)c;
            c = MulAddW1(c, Y.X0.X7, bb, X.X3);
            Y.X0.X7 = (uint)c;
            c = MulAddW1(c, Y.X1.X0, bb, X.X4);
            Y.X1.X0 = (uint)c;
            c = MulAddW1(c, Y.X1.X1, bb, X.X5);
            Y.X1.X1 = (uint)c;
            c = MulAddW1(c, Y.X1.X2, bb, X.X6);
            Y.X1.X2 = (uint)c;
            c = MulAddW1(c, Y.X1.X3, bb, X.X7);
            Y.X1.X3 = (uint)c;

            Y.X1.X4 = (uint)(c >> 32);

            return Y;

            #endregion
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static U32_16 MulAdd5(U32_16 Y, uint b, UInt256 X)
        {
            ulong c;

            #region Hate doing this (prevents local memory allocation somehow?)

            ulong bb = b;

            c = MulAddW0(Y.X0.X5, bb, X.X0);
            Y.X0.X5 = (uint)c;

            c = MulAddW1(c, Y.X0.X6, bb, X.X1);
            Y.X0.X6 = (uint)c;

            c = MulAddW1(c, Y.X0.X7, bb, X.X2);
            Y.X0.X7 = (uint)c;
            c = MulAddW1(c, Y.X1.X0, bb, X.X3);
            Y.X1.X0 = (uint)c;
            c = MulAddW1(c, Y.X1.X1, bb, X.X4);
            Y.X1.X1 = (uint)c;
            c = MulAddW1(c, Y.X1.X2, bb, X.X5);
            Y.X1.X2 = (uint)c;
            c = MulAddW1(c, Y.X1.X3, bb, X.X6);
            Y.X1.X3 = (uint)c;
            c = MulAddW1(c, Y.X1.X4, bb, X.X7);
            Y.X1.X4 = (uint)c;

            Y.X1.X5 = (uint)(c >> 32);

            return Y;

            #endregion
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static U32_16 MulAdd6(U32_16 Y, uint b, UInt256 X)
        {
            ulong c;

            #region Hate doing this (prevents local memory allocation somehow?)

            ulong bb = b;

            c = MulAddW0(Y.X0.X6, bb, X.X0);
            Y.X0.X6 = (uint)c;

            c = MulAddW1(c, Y.X0.X7, bb, X.X1);
            Y.X0.X7 = (uint)c;
            c = MulAddW1(c, Y.X1.X0, bb, X.X2);
            Y.X1.X0 = (uint)c;
            c = MulAddW1(c, Y.X1.X1, bb, X.X3);
            Y.X1.X1 = (uint)c;
            c = MulAddW1(c, Y.X1.X2, bb, X.X4);
            Y.X1.X2 = (uint)c;
            c = MulAddW1(c, Y.X1.X3, bb, X.X5);
            Y.X1.X3 = (uint)c;
            c = MulAddW1(c, Y.X1.X4, bb, X.X6);
            Y.X1.X4 = (uint)c;
            c = MulAddW1(c, Y.X1.X5, bb, X.X7);
            Y.X1.X5 = (uint)c;

            Y.X1.X6 = (uint)(c >> 32);

            return Y;

            #endregion
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static U32_16 MulAdd7(U32_16 Y, uint b, UInt256 X)
        {
            ulong c;

            #region Hate doing this (prevents local memory allocation somehow?)

            ulong bb = b;

            c = MulAddW0(Y.X0.X7, bb, X.X0);
            Y.X0.X7 = (uint)c;

            c = MulAddW1(c, Y.X1.X0, bb, X.X1);
            Y.X1.X0 = (uint)c;
            c = MulAddW1(c, Y.X1.X1, bb, X.X2);
            Y.X1.X1 = (uint)c;
            c = MulAddW1(c, Y.X1.X2, bb, X.X3);
            Y.X1.X2 = (uint)c;
            c = MulAddW1(c, Y.X1.X3, bb, X.X4);
            Y.X1.X3 = (uint)c;
            c = MulAddW1(c, Y.X1.X4, bb, X.X5);
            Y.X1.X4 = (uint)c;
            c = MulAddW1(c, Y.X1.X5, bb, X.X6);
            Y.X1.X5 = (uint)c;
            c = MulAddW1(c, Y.X1.X6, bb, X.X7);
            Y.X1.X6 = (uint)c;

            Y.X1.X7 = (uint)(c >> 32);

            return Y;

            #endregion
        }

        #endregion

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static UInt256 MulAddReduce(ulong c, UInt256 Z, U32_8 Y, uint b, U32_8 X)
        {
            unchecked
            {
                c = MulAddW0(Y.X0, b, X.X0);
                Z.X0 = (uint)c;

                c = MulAddW1(c, Y.X1, b, X.X1);
                Z.X1 = (uint)c;
                c = MulAddW1(c, Y.X2, b, X.X2);
                Z.X2 = (uint)c;
                c = MulAddW1(c, Y.X3, b, X.X3);
                Z.X3 = (uint)c;
                c = MulAddW1(c, Y.X4, b, X.X4);
                Z.X4 = (uint)c;
                c = MulAddW1(c, Y.X5, b, X.X5);
                Z.X5 = (uint)c;
                c = MulAddW1(c, Y.X6, b, X.X6);
                Z.X6 = (uint)c;
                c = MulAddW1(c, Y.X7, b, X.X7);
                Z.X7 = (uint)c;

                for (int i = 0; i < 2; i++)
                {
                    c = MulAddW0(Z.X0, (uint)(c >> 32), 38);
                    Z.X0 = (uint)c;

                    c = AddC1(c, Z.X1);
                    Z.X1 = (uint)c;
                    c = AddC1(c, Z.X2);
                    Z.X2 = (uint)c;
                    c = AddC1(c, Z.X3);
                    Z.X3 = (uint)c;
                    c = AddC1(c, Z.X4);
                    Z.X4 = (uint)c;
                    c = AddC1(c, Z.X5);
                    Z.X5 = (uint)c;
                    c = AddC1(c, Z.X6);
                    Z.X6 = (uint)c;
                    c = AddC1(c, Z.X7);
                    Z.X7 = (uint)c;

                }
            }

            return Z;
        }

        #endregion

        #region Add

        private static void AddReduce_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var z = (RegisterAllocator<PTXRegisterKind>.CompoundRegister)codeGenerator.Allocate(value);
            var x = (RegisterAllocator<PTXRegisterKind>.CompoundRegister)codeGenerator.Load(value[1]);
            var y = (RegisterAllocator<PTXRegisterKind>.CompoundRegister)codeGenerator.Load(value[2]);

            codeGenerator.Builder.AppendLine("{");
            codeGenerator.Builder.AppendLine(".reg .u32 temp;");


            #region Add

            codeGenerator.BeginCommand($"add.cc.u32 {GetStringRepresentation(z.Children[0])}, {GetStringRepresentation(x.Children[0])}, {GetStringRepresentation(y.Children[0])}").Dispose();

            for (int i = 1; i < 8; i++)
            {
                codeGenerator.BeginCommand($"addc.cc.u32 {GetStringRepresentation(z.Children[i])}, {GetStringRepresentation(x.Children[i])}, {GetStringRepresentation(y.Children[i])}").Dispose();
            }

            codeGenerator.BeginCommand($"addc.u32 temp, 0, 0").Dispose();
            codeGenerator.BeginCommand($"mul.lo.u32 temp, temp, 38").Dispose();
            codeGenerator.BeginCommand($"add.cc.u32 {GetStringRepresentation(z.Children[0])}, {GetStringRepresentation(z.Children[0])}, temp").Dispose();

            for (int i = 1; i < 8; i++)
            {
                codeGenerator.BeginCommand($"addc.cc.u32 {GetStringRepresentation(z.Children[i])}, {GetStringRepresentation(z.Children[i])}, 0").Dispose();
            }

            #endregion

            //ulong hi = (ret.Item2 * 38);
            //ulong c = hi << 32;

            //Z = ret.Item1;

            //c = AddC0(Z.X0, hi);
            //Z.X0 = (uint)c;

            //c = AddC1(c, Z.X1);
            //Z.X1 = (uint)c;
            //c = AddC1(c, Z.X2);
            //Z.X2 = (uint)c;
            //c = AddC1(c, Z.X3);
            //Z.X3 = (uint)c;
            //c = AddC1(c, Z.X4);
            //Z.X4 = (uint)c;
            //c = AddC1(c, Z.X5);
            //Z.X5 = (uint)c;
            //c = AddC1(c, Z.X6);
            //Z.X6 = (uint)c;
            //c = AddC1(c, Z.X7);
            //Z.X7 = (uint)c;

            //private static (UInt256, uint) Add(UInt256 Z, UInt256 X, UInt256 Y)
            //{
            //    ulong c = 0;

            //    unchecked
            //    {
            //        c = Add32(X.X0, Y.X0);
            //        Z.X0 = (uint)c;

            //        c = Adc32(c, X.X1, Y.X1);
            //        Z.X1 = (uint)c;
            //        c = Adc32(c, X.X2, Y.X2);
            //        Z.X2 = (uint)c;
            //        c = Adc32(c, X.X3, Y.X3);
            //        Z.X3 = (uint)c;
            //        c = Adc32(c, X.X4, Y.X4);
            //        Z.X4 = (uint)c;
            //        c = Adc32(c, X.X5, Y.X5);
            //        Z.X5 = (uint)c;
            //        c = Adc32(c, X.X6, Y.X6);
            //        Z.X6 = (uint)c;
            //        c = Adc32(c, X.X7, Y.X7);
            //        Z.X7 = (uint)c;
            //    }

            //    return (Z, (uint)(c >> 32));

            //}

            codeGenerator.Builder.AppendLine("}");

            var test = codeGenerator.Builder.ToString();
        }

        [IntrinsicMethod(nameof(AddReduce_Generate))]
        [IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static UInt256 AddReduce(UInt256 Z, UInt256 X, UInt256 Y)
        {
            var ret = Add(Z, X, Y);

            ulong hi = (ret.Item2 * 38);
            ulong c = hi << 32;

            Z = ret.Item1;

            c = AddC0(Z.X0, hi);
            Z.X0 = (uint)c;

            c = AddC1(c, Z.X1);
            Z.X1 = (uint)c;
            c = AddC1(c, Z.X2);
            Z.X2 = (uint)c;
            c = AddC1(c, Z.X3);
            Z.X3 = (uint)c;
            c = AddC1(c, Z.X4);
            Z.X4 = (uint)c;
            c = AddC1(c, Z.X5);
            Z.X5 = (uint)c;
            c = AddC1(c, Z.X6);
            Z.X6 = (uint)c;
            c = AddC1(c, Z.X7);
            Z.X7 = (uint)c;

            return Z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (UInt256, uint) Add(UInt256 Z, UInt256 X, UInt256 Y)
        {
            ulong c = 0;

            unchecked
            {
                c = Add32(X.X0, Y.X0);
                Z.X0 = (uint)c;

                c = Adc32(c, X.X1, Y.X1);
                Z.X1 = (uint)c;
                c = Adc32(c, X.X2, Y.X2);
                Z.X2 = (uint)c;
                c = Adc32(c, X.X3, Y.X3);
                Z.X3 = (uint)c;
                c = Adc32(c, X.X4, Y.X4);
                Z.X4 = (uint)c;
                c = Adc32(c, X.X5, Y.X5);
                Z.X5 = (uint)c;
                c = Adc32(c, X.X6, Y.X6);
                Z.X6 = (uint)c;
                c = Adc32(c, X.X7, Y.X7);
                Z.X7 = (uint)c;
            }

            return (Z, (uint)(c >> 32));

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong AddC0(uint X, ulong V)
        {
            //(U64)(X) + (V); Y = c.u32.lo;
            unchecked
            {
                return (ulong)X + V;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong AddC1(ulong c, uint X)
        {
            //c.u64 = (U64)(X) + c.u32.hi; Y = c.u32.lo;

            unchecked
            {
                return (ulong)X + (c >> 32);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Adc32(ulong c, uint X, uint Y)
        {
            unchecked
            {
                return (ulong)X + (ulong)Y + (c >> 32);
            }

            //c.u64 = (U64)(X) + (U64)(Y) + c.u32.hi; Z = c.u32.lo;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Add32(uint X, uint Y)
        {
            //c.u64 = (U64)(X) + (Y); Z = c.u32.lo;

            unchecked
            {
                return (ulong)X + (ulong)Y;
            }
        }

        #endregion

        #region Sub


        private static void SubReduce_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var z = (RegisterAllocator<PTXRegisterKind>.CompoundRegister)codeGenerator.Allocate(value);
            var x = (RegisterAllocator<PTXRegisterKind>.CompoundRegister)codeGenerator.Load(value[1]);
            var y = (RegisterAllocator<PTXRegisterKind>.CompoundRegister)codeGenerator.Load(value[2]);

            codeGenerator.Builder.AppendLine("{");
            codeGenerator.Builder.AppendLine(".reg .u32 temp;");


            #region Add

            codeGenerator.BeginCommand($"sub.cc.u32 {GetStringRepresentation(z.Children[0])}, {GetStringRepresentation(x.Children[0])}, {GetStringRepresentation(y.Children[0])}").Dispose();

            for (int i = 1; i < 8; i++)
            {
                codeGenerator.BeginCommand($"subc.cc.u32 {GetStringRepresentation(z.Children[i])}, {GetStringRepresentation(x.Children[i])}, {GetStringRepresentation(y.Children[i])}").Dispose();
            }

            codeGenerator.BeginCommand($"subc.u32 temp, 0, 0").Dispose();
            codeGenerator.BeginCommand($"and.b32 temp, temp, 38").Dispose();
            codeGenerator.BeginCommand($"sub.cc.u32 {GetStringRepresentation(z.Children[0])}, {GetStringRepresentation(z.Children[0])}, temp").Dispose();

            for (int i = 1; i < 8; i++)
            {
                codeGenerator.BeginCommand($"subc.cc.u32 {GetStringRepresentation(z.Children[i])}, {GetStringRepresentation(z.Children[i])}, 0").Dispose();
            }

            #endregion

            codeGenerator.Builder.AppendLine("}");

            var test = codeGenerator.Builder.ToString();
        }

        [IntrinsicMethod(nameof(SubReduce_Generate))]
        [IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static UInt256 SubReduce(UInt256 Z, UInt256 X, UInt256 Y)
        {
            //c.u32.hi = ecp_Sub(Z, X, Y) & 38;
            var ret = Sub(Z, X, Y);

            ulong hi = ret.Item2 & 38;

            ulong c = hi << 32;
            Z = ret.Item1;

            c = SubC0(c, Z.X0, hi);
            Z.X0 = (uint)c;

            c = SubC1(c, Z.X1);
            Z.X1 = (uint)c;
            c = SubC1(c, Z.X2);
            Z.X2 = (uint)c;
            c = SubC1(c, Z.X3);
            Z.X3 = (uint)c;
            c = SubC1(c, Z.X4);
            Z.X4 = (uint)c;
            c = SubC1(c, Z.X5);
            Z.X5 = (uint)c;
            c = SubC1(c, Z.X6);
            Z.X6 = (uint)c;
            c = SubC1(c, Z.X7);
            Z.X7 = (uint)c;

            c = SubC0(c, Z.X0, ((uint)(c >> 32) & 38));
            Z.X0 = (uint)c;

            c = SubC1(c, Z.X1);
            Z.X1 = (uint)c;
            c = SubC1(c, Z.X2);
            Z.X2 = (uint)c;
            c = SubC1(c, Z.X3);
            Z.X3 = (uint)c;
            c = SubC1(c, Z.X4);
            Z.X4 = (uint)c;
            c = SubC1(c, Z.X5);
            Z.X5 = (uint)c;
            c = SubC1(c, Z.X6);
            Z.X6 = (uint)c;
            c = SubC1(c, Z.X7);
            Z.X7 = (uint)c;

            return Z;
        }


        private static void MaxPSubReduce_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var z = (RegisterAllocator<PTXRegisterKind>.CompoundRegister)codeGenerator.Allocate(value);
            var y = (RegisterAllocator<PTXRegisterKind>.CompoundRegister)codeGenerator.Load(value[1]);

            codeGenerator.Builder.AppendLine("{");
            codeGenerator.Builder.AppendLine(".reg .u32 temp;");


            #region Add

            codeGenerator.BeginCommand($"sub.cc.u32 {GetStringRepresentation(z.Children[0])}, 0xFFFFFFDA, {GetStringRepresentation(y.Children[0])}").Dispose();

            for (int i = 1; i < 8; i++)
            {
                codeGenerator.BeginCommand($"subc.cc.u32 {GetStringRepresentation(z.Children[i])}, 0xFFFFFFFF, {GetStringRepresentation(y.Children[i])}").Dispose();
            }

            codeGenerator.BeginCommand($"subc.u32 temp, 0, 0").Dispose();
            codeGenerator.BeginCommand($"and.b32 temp, temp, 38").Dispose();
            codeGenerator.BeginCommand($"sub.cc.u32 {GetStringRepresentation(z.Children[0])}, {GetStringRepresentation(z.Children[0])}, temp").Dispose();

            for (int i = 1; i < 8; i++)
            {
                codeGenerator.BeginCommand($"subc.cc.u32 {GetStringRepresentation(z.Children[i])}, {GetStringRepresentation(z.Children[i])}, 0").Dispose();
            }

            #endregion

            codeGenerator.Builder.AppendLine("}");

            var test = codeGenerator.Builder.ToString();
        }

        [IntrinsicMethod(nameof(MaxPSubReduce_Generate))]
        [IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static UInt256 MaxPSubReduce(UInt256 Z, UInt256 Y)
        {
            //c.u32.hi = ecp_Sub(Z, X, Y) & 38;
            var ret = SubMaxP(Z, Y);

            ulong c = (ret.Item2 & 38) << 32;
            Z = ret.Item1;


            c = SubC0(c, Z.X0, (uint)(c >> 32));
            Z.X0 = (uint)c;

            c = SubC1(c, Z.X1);
            Z.X1 = (uint)c;
            c = SubC1(c, Z.X2);
            Z.X2 = (uint)c;
            c = SubC1(c, Z.X3);
            Z.X3 = (uint)c;
            c = SubC1(c, Z.X4);
            Z.X4 = (uint)c;
            c = SubC1(c, Z.X5);
            Z.X5 = (uint)c;
            c = SubC1(c, Z.X6);
            Z.X6 = (uint)c;
            c = SubC1(c, Z.X7);
            Z.X7 = (uint)c;

            c = SubC0(c, Z.X0, (uint)((c >> 32) & 38));
            Z.X0 = (uint)c;

            c = SubC1(c, Z.X1);
            Z.X1 = (uint)c;
            c = SubC1(c, Z.X2);
            Z.X2 = (uint)c;
            c = SubC1(c, Z.X3);
            Z.X3 = (uint)c;
            c = SubC1(c, Z.X4);
            Z.X4 = (uint)c;
            c = SubC1(c, Z.X5);
            Z.X5 = (uint)c;
            c = SubC1(c, Z.X6);
            Z.X6 = (uint)c;
            c = SubC1(c, Z.X7);
            Z.X7 = (uint)c;

            return Z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Sub32(uint X, uint Y)
        {
            // b.s64 = (S64)(X) - (Y); Z = b.s32.lo;
            unchecked
            {
                return (ulong)X - Y;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Sbc32(ulong c, uint X, uint Y)
        {
            //b.s64 = (S64)(X) - (U64)(Y) + b.s32.hi; Z = b.s32.lo;
            unchecked
            {
                return (ulong)X - Y + (ulong)(int)(c >> 32);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (UInt256, ulong) Sub(UInt256 Z, UInt256 X, UInt256 Y)
        {
            ulong c = 0;

            unchecked
            {
                c = Sub32(X.X0, Y.X0);
                Z.X0 = (uint)c;

                c = Sbc32(c, X.X1, Y.X1);
                Z.X1 = (uint)c;
                c = Sbc32(c, X.X2, Y.X2);
                Z.X2 = (uint)c;
                c = Sbc32(c, X.X3, Y.X3);
                Z.X3 = (uint)c;
                c = Sbc32(c, X.X4, Y.X4);
                Z.X4 = (uint)c;
                c = Sbc32(c, X.X5, Y.X5);
                Z.X5 = (uint)c;
                c = Sbc32(c, X.X6, Y.X6);
                Z.X6 = (uint)c;
                c = Sbc32(c, X.X7, Y.X7);
                Z.X7 = (uint)c;
            }

            return (Z, (c >> 32));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (UInt256, ulong) SubMaxP(UInt256 Z, UInt256 Y)
        {
            ulong c = 0;

            c = Sub32(0xFFFFFFDA, Y.X0);
            Z.X0 = (uint)c;

            c = Sbc32(c, 0xFFFFFFFF, Y.X1);
            Z.X1 = (uint)c;
            c = Sbc32(c, 0xFFFFFFFF, Y.X2);
            Z.X2 = (uint)c;
            c = Sbc32(c, 0xFFFFFFFF, Y.X3);
            Z.X3 = (uint)c;
            c = Sbc32(c, 0xFFFFFFFF, Y.X4);
            Z.X4 = (uint)c;
            c = Sbc32(c, 0xFFFFFFFF, Y.X5);
            Z.X5 = (uint)c;
            c = Sbc32(c, 0xFFFFFFFF, Y.X6);
            Z.X6 = (uint)c;
            c = Sbc32(c, 0xFFFFFFFF, Y.X7);
            Z.X7 = (uint)c;

            return (Z, (c >> 32));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong SubC0(ulong c, uint X, ulong V)
        {
            //c.s64 = (U64)(X) - (V); Y = c.u32.lo;
            unchecked
            {
                return (X - V);
            }

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong SubC1(ulong c, uint X)
        {
            //c.s64 = (U64)(X) + (S64)c.s32.hi; Y = c.u32.lo;
            unchecked
            {
                return (ulong)X + (ulong)(int)(c >> 32);
            }
        }

        #endregion

        #region Fold


        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ecp_8Folds(UInt256 X, ArrayView<byte> Y)
        {
            unchecked
            {
                uint a = 0;

                for (int i = 31; i >= 0; --i)
                {
                    a = ((a << 1) + ((X.X7 >> i) & 1)) & 0xFF;
                    a = ((a << 1) + ((X.X6 >> i) & 1)) & 0xFF;
                    a = ((a << 1) + ((X.X5 >> i) & 1)) & 0xFF;
                    a = ((a << 1) + ((X.X4 >> i) & 1)) & 0xFF;
                    a = ((a << 1) + ((X.X3 >> i) & 1)) & 0xFF;
                    a = ((a << 1) + ((X.X2 >> i) & 1)) & 0xFF;
                    a = ((a << 1) + ((X.X1 >> i) & 1)) & 0xFF;
                    a = ((a << 1) + ((X.X0 >> i) & 1)) & 0xFF;

                    byte bb = (byte)a;

                    Y[31 - i] = bb;
                }

                return;
            }
        }

        #endregion

        #endregion

        #region Helpers

        internal static string GetStringRepresentation(RegisterAllocator<PTXRegisterKind>.Register reg)
        {
            if (reg is RegisterAllocator<PTXRegisterKind>.ConstantRegister constantReg)
            {
                return constantReg.Value.Int32Value.ToString();
            }

            return GetStringRepresentation((RegisterAllocator<PTXRegisterKind>.HardwareRegister)reg);
        }

        internal static string GetStringRepresentation(RegisterAllocator<PTXRegisterKind>.PrimitiveRegister v)
        {
            return "%" + PTXRegisterAllocator.GetStringRepresentation((ILGPU.Backends.RegisterAllocator<PTXRegisterKind>.HardwareRegister)v);
        }

        #endregion
    }
}
