global using RegisterId = byte;
using System.Drawing;
using System.Runtime.InteropServices;


namespace DrillX.Compiler
{
    public enum OpCode : int { None = 0xFF, AddShift = 0, Xor, Rotate, Mul, UMulH, SMulH, AddConst, Sub, XorConst, Branch, Target };
    public enum RegisterWriterOp : byte { None, Mul, UMulH, SMulH, AddSub, AddConst, Xor, XorConst, Rotate };

    public struct RegisterWriter
    {
        public RegisterWriterOp Op;
        public uint Value;

        public RegisterWriter(RegisterWriterOp op, uint value = 0)
        {
            Op = op;
            Value = value;
        }
    }

    public struct Instruction
    {
        public const int Loops = 16;
        public const int ProgramSize = 512;
        public const int Size = 16;

        public OpCode Type => (OpCode)Type_;
        public int Dst;
        public int Src;
        public int Type_;

        public int Operand;

        public Instruction(OpCode type, RegisterId src = 0, RegisterId dst = 0, uint operand = 0)
        {
            Dst = dst;
            Src = src;
            Type_ = (int)type;
            Operand = (int)operand;
        }

        public void SetType(OpCode type)
        {
            Type_ = (int)type;
        }

        public void SetOperand(int operand)
        {
            Operand = operand;
        }

        public void SetDestination(ulong dst)
        {
            Dst = (int)dst;
        }


        public RegisterId? Source()
        {
            switch (Type)
            {
                case OpCode.AddShift:
                case OpCode.Mul:
                case OpCode.UMulH:
                case OpCode.SMulH:
                case OpCode.Sub:
                case OpCode.Xor:
                    return (byte)Src;
                case OpCode.AddConst:
                case OpCode.XorConst:
                case OpCode.Rotate:
                case OpCode.Target:
                case OpCode.Branch:
                    return null;
            }

            return null;
        }

        public RegisterId? Destination()
        {
            switch (Type)
            {
                case OpCode.AddShift:
                case OpCode.AddConst:
                case OpCode.Mul:
                case OpCode.UMulH:
                case OpCode.SMulH:
                case OpCode.Sub:
                case OpCode.Xor:
                case OpCode.XorConst:
                case OpCode.Rotate:
                    return (byte)Dst;
                case OpCode.Target:
                case OpCode.Branch:
                    return null;
            }

            return null;
        }


    }

    //public struct Instruction
    //{
    //    public int Operand
    //    {
    //        get { return (int)(Data >> 16); }
    //    }

    //    public OpCode Type
    //    {
    //        get { return (OpCode)((byte)(Data >> 8) & 0xFF); }
    //    }

    //    public ulong Dst
    //    {
    //        get { return Data & 0xFF; }
    //    }

    //    public ulong Src;
    //    public ulong Data;

    //    public Instruction(OpCode type, RegisterId src = 0, RegisterId dst = 0, uint operand = 0)
    //    {
    //        //Type = type;
    //        //Dst = dst;
    //        //Operand = operand;

    //        Data = (ulong)operand << 16 | ((uint)type) << 8 | dst;
    //        Src = src;
    //    }

    //    public void SetType(OpCode type)
    //    {
    //        Data = (Data & 0xFFFFFFFF00FF) | (((uint)type) << 8);
    //    }

    //    public void SetOperand(int operand)
    //    {
    //        Data = (Data & 0x00000000FFFF) | (((ulong)operand) << 16);
    //    }

    //    public void SetDestination(ulong dst)
    //    {
    //        Data = (Data & 0xFFFFFFFFFF00) | (((byte)dst));
    //    }


    //    public RegisterId? Source()
    //    {
    //        switch (Type)
    //        {
    //            case OpCode.AddShift:
    //            case OpCode.Mul:
    //            case OpCode.UMulH:
    //            case OpCode.SMulH:
    //            case OpCode.Sub:
    //            case OpCode.Xor:
    //                return (byte)Src;
    //            case OpCode.AddConst:
    //            case OpCode.XorConst:
    //            case OpCode.Rotate:
    //            case OpCode.Target:
    //            case OpCode.Branch:
    //                return null;
    //        }

    //        return null;
    //    }

    //    public RegisterId? Destination()
    //    {
    //        switch (Type)
    //        {
    //            case OpCode.AddShift:
    //            case OpCode.AddConst:
    //            case OpCode.Mul:
    //            case OpCode.UMulH:
    //            case OpCode.SMulH:
    //            case OpCode.Sub:
    //            case OpCode.Xor:
    //            case OpCode.XorConst:
    //            case OpCode.Rotate:
    //                return (byte)Dst;
    //            case OpCode.Target:
    //            case OpCode.Branch:
    //                return null;
    //        }

    //        return null;
    //    }

    //}


}
