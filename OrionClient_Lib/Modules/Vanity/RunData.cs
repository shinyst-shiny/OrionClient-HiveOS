using ILGPU;
using ILGPU.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules.Vanity
{
    public struct RunData
    {
        public ArrayView<byte> PrivateBuffer;
        public ArrayView<byte> PublicBuffer;
        public ArrayView<byte> VanityBuffer;
        public ArrayView<PA_POINT> Folding_PA;

        public RunData(ArrayView<byte> privateBuffer, ArrayView<byte> publicBuffer, ArrayView<byte> vanityBuffer, ArrayView<uint> foldingBuffer)
        {
            PrivateBuffer = privateBuffer;
            PublicBuffer = publicBuffer;
            Folding_PA = foldingBuffer.Cast<PA_POINT>().AsDense();
            VanityBuffer = vanityBuffer;
        }
    }

    public struct UInt256
    {
        public uint X0;
        public uint X1;
        public uint X2;
        public uint X3;
        public uint X4;
        public uint X5;
        public uint X6;
        public uint X7;
    }

    public struct PA_POINT
    {
        public UInt256 YpX;
        public UInt256 YmX;
        public UInt256 T2d;
    }

    public struct Ext_POINT
    {
        public UInt256 X;
        public UInt256 Y;
        public UInt256 Z;
        public UInt256 T;
    }

    public struct U32_8
    {
        public uint X0;
        public uint X1;
        public uint X2;
        public uint X3;
        public uint X4;
        public uint X5;
        public uint X6;
        public uint X7;
    }

    public struct U32_16
    {
        public U32_8 X0;
        public U32_8 X1;
    }

}
