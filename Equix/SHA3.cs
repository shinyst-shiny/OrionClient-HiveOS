using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Equix
{
    public class SHA3
    {
        private static readonly ulong[] RoundConstants = new ulong[]
        {
            0x0000000000000001UL,
            0x0000000000008082UL,
            0x800000000000808aUL,
            0x8000000080008000UL,
            0x000000000000808bUL,
            0x0000000080000001UL,
            0x8000000080008081UL,
            0x8000000000008009UL,
            0x000000000000008aUL,
            0x0000000000000088UL,
            0x0000000080008009UL,
            0x000000008000000aUL,
            0x000000008000808bUL,
            0x800000000000008bUL,
            0x8000000000008089UL,
            0x8000000000008003UL,
            0x8000000000008002UL,
            0x8000000000000080UL,
            0x000000000000800aUL,
            0x800000008000000aUL,
            0x8000000080008081UL,
            0x8000000000008080UL,
            0x0000000080000001UL,
            0x8000000080008008UL
        };

        public static void Sha3Hash(Span<ushort> hash, ulong nonce, Span<byte> output)
        {
            var h = MemoryMarshal.Cast<ushort, ulong>(hash);

            ulong a00 = h[0];
            ulong a01 = h[1];
            ulong a02 = nonce;
            ulong a03 = 1;
            ulong a04 = 0;
            ulong a10 = 0;
            ulong a11 = 0;
            ulong a12 = 0;
            ulong a13 = 0;
            ulong a14 = 0;
            ulong a20 = 0;
            ulong a21 = 0;
            ulong a22 = 0;
            ulong a23 = 0;
            ulong a24 = 0;
            ulong a30 = 0;
            ulong a31 = 9223372036854775808;
            ulong a32 = 0;
            ulong a33 = 0;
            ulong a34 = 0;
            ulong a40 = 0;
            ulong a41 = 0;
            ulong a42 = 0;
            ulong a43 = 0;
            ulong a44 = 0;

            for (int round = 0; round < 23; round++)
            {
                // Theta

                ulong bc0 = a00 ^ a10 ^ a20 ^ a30 ^ a40;
                ulong bc1 = a01 ^ a11 ^ a21 ^ a31 ^ a41;
                ulong bc2 = a02 ^ a12 ^ a22 ^ a32 ^ a42;
                ulong bc3 = a03 ^ a13 ^ a23 ^ a33 ^ a43;
                ulong bc4 = a04 ^ a14 ^ a24 ^ a34 ^ a44;

                ulong t;

                t = bc4 ^ ROL(bc1, 1); a00 ^= t; a10 ^= t; a20 ^= t; a30 ^= t; a40 ^= t;
                t = bc0 ^ ROL(bc2, 1); a01 ^= t; a11 ^= t; a21 ^= t; a31 ^= t; a41 ^= t;
                t = bc1 ^ ROL(bc3, 1); a02 ^= t; a12 ^= t; a22 ^= t; a32 ^= t; a42 ^= t;
                t = bc2 ^ ROL(bc4, 1); a03 ^= t; a13 ^= t; a23 ^= t; a33 ^= t; a43 ^= t;
                t = bc3 ^ ROL(bc0, 1); a04 ^= t; a14 ^= t; a24 ^= t; a34 ^= t; a44 ^= t;

                // Rho Pi

                t = a01;

                Rho_Pi(ref t, ref bc0, ref a20, 1);
                Rho_Pi(ref t, ref bc0, ref a12, 3);
                Rho_Pi(ref t, ref bc0, ref a21, 6);
                Rho_Pi(ref t, ref bc0, ref a32, 10);
                Rho_Pi(ref t, ref bc0, ref a33, 15);
                Rho_Pi(ref t, ref bc0, ref a03, 21);
                Rho_Pi(ref t, ref bc0, ref a10, 28);
                Rho_Pi(ref t, ref bc0, ref a31, 36);
                Rho_Pi(ref t, ref bc0, ref a13, 45);
                Rho_Pi(ref t, ref bc0, ref a41, 55);
                Rho_Pi(ref t, ref bc0, ref a44, 2);
                Rho_Pi(ref t, ref bc0, ref a04, 14);
                Rho_Pi(ref t, ref bc0, ref a30, 27);
                Rho_Pi(ref t, ref bc0, ref a43, 41);
                Rho_Pi(ref t, ref bc0, ref a34, 56);
                Rho_Pi(ref t, ref bc0, ref a23, 8);
                Rho_Pi(ref t, ref bc0, ref a22, 25);
                Rho_Pi(ref t, ref bc0, ref a02, 43);
                Rho_Pi(ref t, ref bc0, ref a40, 62);
                Rho_Pi(ref t, ref bc0, ref a24, 18);
                Rho_Pi(ref t, ref bc0, ref a42, 39);
                Rho_Pi(ref t, ref bc0, ref a14, 61);
                Rho_Pi(ref t, ref bc0, ref a11, 20);
                Rho_Pi(ref t, ref bc0, ref a01, 44);

                //  Chi

                bc0 = a00; bc1 = a01; bc2 = a02; bc3 = a03; bc4 = a04;
                a00 ^= ~bc1 & bc2; a01 ^= ~bc2 & bc3; a02 ^= ~bc3 & bc4; a03 ^= ~bc4 & bc0; a04 ^= ~bc0 & bc1;

                bc0 = a10; bc1 = a11; bc2 = a12; bc3 = a13; bc4 = a14;
                a10 ^= ~bc1 & bc2; a11 ^= ~bc2 & bc3; a12 ^= ~bc3 & bc4; a13 ^= ~bc4 & bc0; a14 ^= ~bc0 & bc1;

                bc0 = a20; bc1 = a21; bc2 = a22; bc3 = a23; bc4 = a24;
                a20 ^= ~bc1 & bc2; a21 ^= ~bc2 & bc3; a22 ^= ~bc3 & bc4; a23 ^= ~bc4 & bc0; a24 ^= ~bc0 & bc1;

                bc0 = a30; bc1 = a31; bc2 = a32; bc3 = a33; bc4 = a34;
                a30 ^= ~bc1 & bc2; a31 ^= ~bc2 & bc3; a32 ^= ~bc3 & bc4; a33 ^= ~bc4 & bc0; a34 ^= ~bc0 & bc1;

                bc0 = a40; bc1 = a41; bc2 = a42; bc3 = a43; bc4 = a44;
                a40 ^= ~bc1 & bc2; a41 ^= ~bc2 & bc3; a42 ^= ~bc3 & bc4; a43 ^= ~bc4 & bc0; a44 ^= ~bc0 & bc1;

                //  Iota

                a00 ^= RoundConstants[round];
            }

            // Theta
            {
                ulong bc0 = a00 ^ a10 ^ a20 ^ a30 ^ a40;
                ulong bc1 = a01 ^ a11 ^ a21 ^ a31 ^ a41;
                ulong bc2 = a02 ^ a12 ^ a22 ^ a32 ^ a42;
                ulong bc3 = a03 ^ a13 ^ a23 ^ a33 ^ a43;
                ulong bc4 = a04 ^ a14 ^ a24 ^ a34 ^ a44;

                ulong t;

                t = bc4 ^ ROL(bc1, 1); a00 ^= t; a10 ^= t; a20 ^= t; a30 ^= t; a40 ^= t;
                t = bc0 ^ ROL(bc2, 1); a01 ^= t; a11 ^= t; a21 ^= t; a31 ^= t; a41 ^= t;
                t = bc1 ^ ROL(bc3, 1); a02 ^= t; a12 ^= t; a22 ^= t; a32 ^= t; a42 ^= t;
                t = bc2 ^ ROL(bc4, 1); a03 ^= t; a13 ^= t; a23 ^= t; a33 ^= t; a43 ^= t;
                t = bc3 ^ ROL(bc0, 1); a04 ^= t; a14 ^= t; a24 ^= t; a34 ^= t; a44 ^= t;

                // Rho Pi

                t = a01;

                Rho_Pi(ref t, ref bc0, ref a20, 1);
                Rho_Pi(ref t, ref bc0, ref a12, 3);
                Rho_Pi(ref t, ref bc0, ref a21, 6);
                Rho_Pi(ref t, ref bc0, ref a32, 10);
                Rho_Pi(ref t, ref bc0, ref a33, 15);
                Rho_Pi(ref t, ref bc0, ref a03, 21);
                Rho_Pi(ref t, ref bc0, ref a10, 28);
                Rho_Pi(ref t, ref bc0, ref a31, 36);
                Rho_Pi(ref t, ref bc0, ref a13, 45);
                Rho_Pi(ref t, ref bc0, ref a41, 55);
                Rho_Pi(ref t, ref bc0, ref a44, 2);
                Rho_Pi(ref t, ref bc0, ref a04, 14);
                Rho_Pi(ref t, ref bc0, ref a30, 27);
                Rho_Pi(ref t, ref bc0, ref a43, 41);
                Rho_Pi(ref t, ref bc0, ref a34, 56);
                Rho_Pi(ref t, ref bc0, ref a23, 8);
                Rho_Pi(ref t, ref bc0, ref a22, 25);
                Rho_Pi(ref t, ref bc0, ref a02, 43);
                Rho_Pi(ref t, ref bc0, ref a40, 62);
                Rho_Pi(ref t, ref bc0, ref a24, 18);
                Rho_Pi(ref t, ref bc0, ref a42, 39);
                Rho_Pi(ref t, ref bc0, ref a14, 61);
                Rho_Pi(ref t, ref bc0, ref a11, 20);
                Rho_Pi(ref t, ref bc0, ref a01, 44);

                //  Chi
                bc0 = a00; bc1 = a01; bc2 = a02; bc3 = a03; bc4 = a04;
                a00 ^= ~bc1 & bc2; a01 ^= ~bc2 & bc3; a02 ^= ~bc3 & bc4; a03 ^= ~bc4 & bc0; a04 ^= ~bc0 & bc1;

                //  Iota
                a00 ^= RoundConstants[23];
            }


            Span<ulong> o = MemoryMarshal.Cast<byte, ulong>(output);
            o[0] = a00;
            o[1] = a01;
            o[2] = a02;
            o[3] = a03;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Rho_Pi(ref ulong t, ref ulong bc0, ref ulong ad, int r)
        {
            bc0 = ad;
            ad = ROL(t, r);
            t = bc0;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong ROL(ulong a, int offset)
        {
            return ((a) << (offset)) ^ ((a) >> (64 - (offset)));
        }
    }
}
