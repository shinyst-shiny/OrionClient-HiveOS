global using ExecPorts = byte;
global using ExecPortIndex = byte;

using System.Collections;
using System.Runtime.CompilerServices;

namespace DrillX.Compiler
{
    internal struct Scheduler
    {
        public const uint NumRegisters = 8;

        public SubCycle _subCycle;
        public Cycle _cycle;
        private ExecSchedule _exec;
        private DataSchedule _data;

        public Scheduler()
        {
            _exec = new ExecSchedule();
            _data = new DataSchedule();
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal InstructionPlan InstructionPlan(OpCode op)
        {
            return _exec.InstructionPlan(_cycle, op);
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal SubCycle InstructionStreamSubCycle()
        {
            return _subCycle;
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void CommitInstructionPlan(InstructionPlan plan, ref Instruction inst)
        {
            _exec.MarkInstructionBusy(plan);

            var dst = inst.Destination();

            if(dst != null)
            {
                _data.PlanRegisterWrite(dst.Value, plan.CycleRetired(inst.Type));
            }
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool RegisterAvailable(RegisterId reg, Cycle cycle)
        {
            return _data.RegisterAvailable(reg, cycle); 
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Cycle OverallLatency()
        {
            return _data.OverallLatency();
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Stall()
        {
            Advance(SubCycle.PerCycle);
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool Advance(ulong n)
        {
            var subCycle = _subCycle.AddUSize(n);
            var cycle = subCycle.Cycle();

            if(cycle < Cycle.Target)
            {
                _subCycle = subCycle;
                _cycle = cycle;

                return true;
            }

            return false;
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool AdvanceInstructionStream(OpCode type)
        {
            return Advance(Model.InstructionSubCycleCount(type));
        }
    }

    internal struct DataSchedule
    {
        public Cycle[] Latencies;

        public DataSchedule()
        {
            Latencies = new Cycle[Scheduler.NumRegisters];
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PlanRegisterWrite(RegisterId dst, Cycle cycle)
        {
            Latencies[dst] = cycle;
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool RegisterAvailable(RegisterId reg, Cycle cycle)
        {
            return Latencies[reg].Timestamp <= cycle.Timestamp;
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Cycle OverallLatency()
        {
            Cycle max = 0;

            for(int i = 0; i < Latencies.Length; i++)
            {
                var cycle = Latencies[i];

                max = (Cycle)Math.Max(cycle, max);
            }

            return max;
        }
    }

    internal struct ExecSchedule
    {
        public PortSchedule[] Ports;

        public ExecSchedule()
        {
            Ports = new PortSchedule[Model.NumExecutionPorts];

            for(int i = 0; i < Ports.Length; i++)
            {
                Ports[i] = new PortSchedule();
            }
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal MicroOpPlan MicroPlan(Cycle begin, ExecPorts ports)
        {
            var cycle = begin;

            while(true)
            {
                for(int index = 0; index < Model.NumExecutionPorts; index++)
                {
                    if ((ports & (1 << index)) != 0 && !Ports[index].Busy.Get(cycle))
                    {
                        return new MicroOpPlan(cycle, (byte)index);
                    }
                }

                cycle = cycle.AddUSize(1);
            }
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void MarkMicroBusy(MicroOpPlan plan)
        {
            Ports[plan.ExecPortIndex].Busy.Set(plan.Cycle, true);
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void MarkInstructionBusy(InstructionPlan plan)
        {
            (MicroOpPlan first, MicroOpPlan? second) = plan.AsMicroPlan();

            MarkMicroBusy(first);

            if(second.HasValue)
            {
                MarkMicroBusy(second.Value);
            }
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal InstructionPlan InstructionPlan(Cycle begin, OpCode op)
        {
            var mOp = Model.MicroOperations(op);

            if(!mOp.optionalExecPort.HasValue)
            {
                return FromMicroPlans(MicroPlan(begin, mOp.execPort), null, out bool success);
            }

            var cycle = begin;

            while(true)
            {
                var firstPlan = MicroPlan(cycle, mOp.execPort);
                var secondPlan = MicroPlan(cycle, mOp.optionalExecPort.Value);

                var result = FromMicroPlans(firstPlan, secondPlan, out bool success);

                if(success)
                {
                    return result;
                }

                cycle = cycle.AddUSize(1);
            }
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        private InstructionPlan FromMicroPlans(MicroOpPlan firstOp, MicroOpPlan? secondOp, out bool success)
        {
            success = true;
            ExecPortIndex? secondPort = null;

            if(secondOp.HasValue)
            {
                if(firstOp.Cycle == secondOp.Value.Cycle)
                {
                    secondPort = secondOp.Value.ExecPortIndex;
                }
                else
                {
                    success = false;

                    return new InstructionPlan();
                }
            }

            return new InstructionPlan(firstOp.Cycle, firstOp.ExecPortIndex, secondPort);
        }
    }

    internal struct PortSchedule
    {
        public SimpleBitArray Busy;

        public PortSchedule()
        {
            Busy = new SimpleBitArray((64 + Model.ScheduleSize - 1) / 64);
        }
    }

    class SimpleBitArray
    {
        private ulong[] Inner;

        public SimpleBitArray(int size)
        {
            Inner = new ulong[size];
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(int index, bool value)
        {
            var wordSize = sizeof(ulong) * 8;
            var wordIndex = index / wordSize;
            var bitMask = 1ul << (index % wordSize);

            if(value)
            {
                Inner[wordIndex] |= bitMask;
            }
            else
            {
                Inner[wordIndex] &= bitMask;
            }
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Get(int index)
        {
            var wordSize = sizeof(ulong) * 8;
            var wordIndex = index / wordSize;
            var bitMask = 1ul << (index % wordSize);

            return 0 != (Inner[wordIndex] & bitMask);
        }
    }

    internal struct MicroOpPlan
    {
        public Cycle Cycle;
        public ExecPortIndex ExecPortIndex;

        public MicroOpPlan(Cycle cycle, ExecPortIndex execPortIndex)
        {
            Cycle = cycle;
            ExecPortIndex = execPortIndex;
        }
    }

    internal struct Cycle
    {
        public byte Timestamp;
        public static Cycle Target = new Cycle(Model.TargetCycles);

        public Cycle(byte timestamp)
        {
            Timestamp = timestamp;
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Cycle AddUSize(ulong n)
        {
            var result = Timestamp + n;

            if (result < Model.ScheduleSize)
            {
                return new Cycle((byte)result);
            }

            throw new Exception("Cycle type wide enough for for target count");
        }

        public static implicit operator int(Cycle c) => c.Timestamp;
        public static implicit operator Cycle(byte c) => new Cycle(c);
    }


    internal struct SubCycle
    {
        public ushort Timestamp;

        public const ushort PerCycle = 3;
        public const ushort Max = 196 * PerCycle - 1; //587

        public SubCycle(ushort timestamp)
        {
            Timestamp = timestamp;
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Cycle Cycle()
        {
            return (byte)(Timestamp / PerCycle);
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SubCycle AddUSize(ulong n)
        {
            var result = Timestamp + n;

            if(result < Max)
            {
                return new SubCycle((ushort)result);
            }

            throw new Exception("Subcycle type wide enough for full schedule size");
        }
    }

    internal struct InstructionPlan
    {
        public Cycle Cycle;
        public ExecPortIndex FirstPort;
        public ExecPortIndex? SecondPort;

        public InstructionPlan(Cycle cycle, ExecPortIndex firstPort, ExecPortIndex? secondPort = null)
        {
            Cycle = cycle;
            FirstPort = firstPort;
            SecondPort = secondPort;
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Cycle CycleIssued()
        {
            return Cycle;
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Cycle CycleRetired(OpCode code)
        {
            return Cycle.AddUSize(Model.InstructionLatencyCycles(code));
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal (MicroOpPlan plan1, MicroOpPlan? plan2) AsMicroPlan()
        {
            MicroOpPlan plan1 = new MicroOpPlan(Cycle, FirstPort);
            MicroOpPlan? plan2 = null;

            if(SecondPort.HasValue)
            {
                plan2 = new MicroOpPlan(Cycle, SecondPort.Value);
            }

            return (plan1, plan2);
        }
    }
}