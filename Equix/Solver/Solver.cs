using DrillX.Compiler;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Tmds.Linux;
using Windows.Win32;
using static SolverHeap;

namespace DrillX.Solver
{
    public unsafe class Solver : IDisposable
    {
        [DllImport("libequix", EntryPoint = "equix_solver_pre", CallingConvention = CallingConvention.StdCall)]
        static unsafe extern int C_Solve(ulong* v, byte* heap, EquixSolution* output);

        [DllImport("libequix", EntryPoint = "equix_solver_full", CallingConvention = CallingConvention.StdCall)]
        static unsafe extern int C_Solve_Full(void* func, byte* heap, EquixSolution* output, ulong* keyReg);

        [DllImport("libequix", EntryPoint = "equix_solver_full_avx2", CallingConvention = CallingConvention.StdCall)]
        static unsafe extern int C_Solve_Full_Avx2(void* func, byte* heap, EquixSolution* output, ulong* keyReg);

        //[DllImport("libequix", EntryPoint = "equix_solver_full_avx512", CallingConvention = CallingConvention.StdCall)]
        //static unsafe extern int C_Solve_Full_Avx512(void* func, byte* heap, EquixSolution* output, ulong* keyReg);

        public enum EquixResult { EquixPartialSum, EquixFinalSum, EquixOk };

        private const ulong EquixStage1Mask = ((1ul << 15) - 1);
        private const ulong EquixStage2Mask = ((1ul << 30) - 1);
        private const ulong EquixFullMask = ((1ul << 60) - 1);

        private const int IndexSpace = 1 << 16;

        public nint Heap { get; private set; }
        public nint ComputeSolutions { get; private set; }
        public nint CompiledProgram { get; private set; }
        public Instruction[] ProgramCache { get; private set; }

        public Solver()
        {
            Heap = (nint)NativeMemory.AlignedAlloc(TotalSize, 64);
            ComputeSolutions = (nint)NativeMemory.AlignedAlloc(MaxComputeSolutionsSize, 64);
            CompiledProgram = (nint)VirtualMemory.HashxVmAlloc(X86Compiler.CodeSize);
            ProgramCache = new Instruction[512];
        }

        private unsafe void SolveStage0(HashX program, void* regs)
        {
            void* h = Heap.ToPointer();

            Span<ushort> stage1IndicesCounts = new Span<ushort>(h, SolverHeap.NumCoarseBuckets);
            Span<ushort> stage1IndicesData = new Span<ushort>(((byte*)h + 512), NumCoarseBuckets * CoarseBucketItems);
            Span<ulong> stage1Data = new Span<ulong>(((byte*)h + 1033216 + 172032), NumCoarseBuckets * CoarseBucketItems);

            stage1IndicesCounts.Clear();

            ulong* hashValues = (ulong*)regs;

            for (int i = 0; i < IndexSpace; i++)
            {
                ulong value = program.AsmCompiled((ulong)i, hashValues);

                //for(int z = 0; z < hashValues.Length; z++)
                {
                    //ulong value = hashValues[z];

                    int bucketIdx = (int)(value % NumCoarseBuckets);
                    ushort itemIdx = stage1IndicesCounts[bucketIdx];

                    if (itemIdx >= CoarseBucketItems)
                    {
                        continue;
                    }

                    stage1IndicesCounts[bucketIdx] = (ushort)(itemIdx + 1);

                    int index = bucketIdx * CoarseBucketItems + itemIdx;

                    stage1IndicesData[index] = (ushort)(i);
                    stage1Data[index] = (value / NumCoarseBuckets) & 0x000FFFFFFFFFFFFF;
                }
            }

            //Console.WriteLine(String.Join(", ", stage1Data.Slice(0, 256).ToArray()));
        }

        private unsafe void SolveStage0_Avx2(HashX program, void* hh)
        {
            void* h = Heap.ToPointer();

            Span<ushort> stage1IndicesCounts = new Span<ushort>(h, SolverHeap.NumCoarseBuckets);
            Span<ushort> stage1IndicesData = new Span<ushort>(((byte*)h + 512), NumCoarseBuckets * CoarseBucketItems);
            Span<ulong> stage1Data = new Span<ulong>(((byte*)h + 1033216 + 172032), NumCoarseBuckets * CoarseBucketItems);

            stage1IndicesCounts.Clear();

            ulong* hashValues = (ulong*)hh;// stackalloc ulong[8];

            for (int i = 0; i < IndexSpace; i += 4)
            {
                program.AsmCompiled_Avx2((ulong)i, hashValues);

                for (int z = 0; z < 4; z++)
                {
                    ulong value = hashValues[z];

                    int bucketIdx = (int)(value % NumCoarseBuckets);
                    ushort itemIdx = stage1IndicesCounts[bucketIdx];

                    if (itemIdx >= CoarseBucketItems)
                    {
                        continue;
                    }

                    stage1IndicesCounts[bucketIdx] = (ushort)(itemIdx + 1);

                    int index = bucketIdx * CoarseBucketItems + itemIdx;

                    stage1IndicesData[index] = (ushort)(i + z);
                    stage1Data[index] = (value / NumCoarseBuckets) & 0x000FFFFFFFFFFFFF;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int InvertBucket(int v)
        {
            return (int)((-v) & 255);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int InvertScratch(int v)
        {
            return (int)((-v) & 127);
        }

        private unsafe void SolveStage1()
        {
            void* h = Heap.ToPointer();

            Span<ushort> stage1IndicesCounts = new Span<ushort>(h, SolverHeap.NumCoarseBuckets);
            Span<ushort> stage2IndicesCounts = new Span<ushort>(((byte*)h + 172544), SolverHeap.NumCoarseBuckets);
            Span<byte> scratchIndicesCounts = new Span<byte>(((byte*)h + 1721856 + 172032), SolverHeap.NumFineBuckets);

            stage2IndicesCounts.Clear();

            //Span<ushort> stage1IndicesData = new Span<ushort>(((byte*)h + 512), NumCoarseBuckets * CoarseBucketItems);
            Span<uint> stage2IndicesData = new Span<uint>(((byte*)h + 173056), NumCoarseBuckets * CoarseBucketItems);

            Span<ulong> stage1Data = new Span<ulong>(((byte*)h + 1033216 + 172032), NumCoarseBuckets * CoarseBucketItems);
            Span<ulong> stage2Data = new Span<ulong>(((byte*)h + 517120), NumCoarseBuckets * CoarseBucketItems);
            Span<ushort> scratchData = new Span<ushort>(((byte*)h + 1721984 + 172032), NumFineBuckets * FineBucketItems);

            for (byte bucketIdx = 1; bucketIdx < (256 / 2 + 1); ++bucketIdx)
            {
                int cplBucket = InvertBucket(bucketIdx);
                scratchIndicesCounts.Clear();

                uint cplBuckSize = stage1IndicesCounts[cplBucket];

                var stage1Index = cplBucket * CoarseBucketItems;

                for (ushort itemIdx = 0; itemIdx < cplBuckSize; ++itemIdx)
                {
                    ulong value = stage1Data[stage1Index + itemIdx];

                    var fineBuckIdx = (int)(value & 127);
                    var fineItemIdx = scratchIndicesCounts[fineBuckIdx];

                    if (fineItemIdx >= FineBucketItems)
                    {
                        continue;
                    }

                    scratchIndicesCounts[fineBuckIdx] = (byte)(fineItemIdx + 1);
                    scratchData[fineBuckIdx * FineBucketItems + fineItemIdx] = itemIdx;


                    if (cplBucket == bucketIdx)
                    {
                        //MakePairs
                        MakePairs(stage1Data, scratchData, scratchIndicesCounts, stage2IndicesCounts, stage2IndicesData, stage2Data, itemIdx, bucketIdx, cplBucket);
                    }
                }

                if (cplBucket != bucketIdx)
                {
                    uint buckSize = stage1IndicesCounts[bucketIdx];

                    for (int itemIdx = 0; itemIdx < buckSize; ++itemIdx)
                    {
                        //MakePairs
                        MakePairs(stage1Data, scratchData, scratchIndicesCounts, stage2IndicesCounts, stage2IndicesData, stage2Data, itemIdx, bucketIdx, cplBucket);
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void MakePairs(Span<ulong> stage1Data, Span<ushort> scratchData, Span<byte> scratchIndicesCount, Span<ushort> stage2Size, Span<uint> stage2IdxData, Span<ulong> stage2Data, int itemIdx, int bucketIdx, int cplBucket)
            {
                ulong value = stage1Data[bucketIdx * CoarseBucketItems + itemIdx];

                if (bucketIdx != 0)
                {
                    value++;
                }

                var fineBuckIdx = (int)(value & 127);
                var fineCplBucket = (int)((-fineBuckIdx) & 127);
                var fineCplSize = scratchIndicesCount[fineCplBucket];

                var scratchDataIndex = fineCplBucket * 12;
                var stage1DataIndex = cplBucket * 336;
                for (int fineIdx = 0; fineIdx < fineCplSize; ++fineIdx)
                {
                    var cplIndex = scratchData[scratchDataIndex + fineIdx];
                    ulong cplValue = stage1Data[stage1DataIndex + cplIndex];
                    ulong sum = value + cplValue;

                    sum /= 128;

                    var s2BuckId = (int)(sum & 255);
                    var s2ItemId = stage2Size[s2BuckId];

                    if (s2ItemId >= 336) { continue; }

                    var bIndex = s2BuckId * 336;
                    stage2Size[s2BuckId] = (ushort)(s2ItemId + 1);
                    stage2IdxData[bIndex + s2ItemId] = (uint)((itemIdx) << 17 | (cplIndex) << 8 | (bucketIdx));

                    stage2Data[bIndex + s2ItemId] = sum / 256;
                }
            }
        }

        private unsafe void SolveStage2()
        {
            void* h = Heap.ToPointer();

            Span<ushort> stage2IndicesCounts = new Span<ushort>(((byte*)h + 172544), SolverHeap.NumCoarseBuckets);
            Span<ushort> stage3IndicesCounts = new Span<ushort>(((byte*)h + 1033216 + 172032), SolverHeap.NumCoarseBuckets);
            Span<byte> scratchIndicesCounts = new Span<byte>(((byte*)h + 1721856 + 172032), SolverHeap.NumFineBuckets);

            stage3IndicesCounts.Clear();

            Span<uint> stage3IndicesData = new Span<uint>(((byte*)h + 1033728 + 172032), NumCoarseBuckets * CoarseBucketItems);

            Span<ulong> stage2Data = new Span<ulong>(((byte*)h + 517120), NumCoarseBuckets * CoarseBucketItems);
            Span<uint> stage3Data = new Span<uint>(((byte*)h + 1377792 + 172032), NumCoarseBuckets * CoarseBucketItems);
            Span<ushort> scratchData = new Span<ushort>(((byte*)h + 1721984 + 172032), NumFineBuckets * FineBucketItems);

            for (byte bucketIdx = 0; bucketIdx < (256 / 2 + 1); ++bucketIdx)
            {
                int cplBucket = InvertBucket(bucketIdx);
                scratchIndicesCounts.Clear();

                uint cplBuckSize = stage2IndicesCounts[cplBucket];

                var stage2Index = cplBucket * CoarseBucketItems;

                for (ushort itemIdx = 0; itemIdx < cplBuckSize; ++itemIdx)
                {
                    ulong value = stage2Data[stage2Index + itemIdx];
                    var fineBuckIdx = (int)(value & 127);
                    var fineItemIdx = scratchIndicesCounts[fineBuckIdx];

                    if (fineItemIdx >= FineBucketItems)
                    {
                        continue;
                    }

                    scratchIndicesCounts[fineBuckIdx] = (byte)(fineItemIdx + 1);
                    scratchData[fineBuckIdx * FineBucketItems + fineItemIdx] = itemIdx;

                    if (cplBucket == bucketIdx)
                    {
                        //MakePairs
                        MakePairs(stage2Data, scratchData, scratchIndicesCounts, stage3IndicesCounts, stage3IndicesData, stage3Data, itemIdx);
                    }
                }

                if (cplBucket != bucketIdx)
                {
                    uint buckSize = stage2IndicesCounts[bucketIdx];

                    for (int itemIdx = 0; itemIdx < buckSize; ++itemIdx)
                    {
                        //MakePairs
                        MakePairs(stage2Data, scratchData, scratchIndicesCounts, stage3IndicesCounts, stage3IndicesData, stage3Data, itemIdx);
                    }
                }

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                void MakePairs(Span<ulong> stage2Data, Span<ushort> scratchData, Span<byte> scratchIndicesCount, Span<ushort> stage3Size, Span<uint> stage3IdxData, Span<uint> stage3Data, int itemIdx)
                {
                    ulong value = stage2Data[bucketIdx * CoarseBucketItems + itemIdx];

                    if (bucketIdx != 0)
                    {
                        value++;
                    }

                    var fineBuckIdx = (int)(value & 127);
                    var fineCplBucket = (int)((-fineBuckIdx) & 127);
                    var fineCplSize = scratchIndicesCount[fineCplBucket];

                    var scratchDataIndex = fineCplBucket * 12;
                    var stage2DataIndex = cplBucket * 336;
                    for (int fineIdx = 0; fineIdx < fineCplSize; ++fineIdx)
                    {
                        var cplIndex = scratchData[scratchDataIndex + fineIdx];
                        ulong cplValue = stage2Data[stage2DataIndex + cplIndex];
                        ulong sum = value + cplValue;

                        sum /= 128;

                        var s3BuckId = (int)(sum & 255);
                        var s3ItemId = stage3Size[s3BuckId];

                        if (s3ItemId >= 336) { continue; }

                        var bIndex = s3BuckId * 336;
                        stage3Size[s3BuckId] = (ushort)(s3ItemId + 1);
                        stage3IdxData[bIndex + s3ItemId] = (uint)((itemIdx) << 17 | (cplIndex) << 8 | (bucketIdx));

                        stage3Data[bIndex + s3ItemId] = (uint)(sum / 256);
                    }
                }
            }
        }

        private unsafe int SolveStage3(Span<EquixSolution> solutions)
        {
            void* h = Heap.ToPointer();

            Span<ushort> stage3IndicesCounts = new Span<ushort>(((byte*)h + 1033216 + 172032), SolverHeap.NumCoarseBuckets);
            Span<uint> stage3IndicesData = new Span<uint>(((byte*)h + 1033728 + 172032), NumCoarseBuckets * CoarseBucketItems);
            Span<uint> stage3Data = new Span<uint>(((byte*)h + 1377792 + 172032), NumCoarseBuckets * CoarseBucketItems);
            Span<byte> scratchIndicesCounts = new Span<byte>(((byte*)h + 1721856 + 172032), SolverHeap.NumFineBuckets);
            Span<ushort> scratchData = new Span<ushort>(((byte*)h + 1721984 + 172032), NumFineBuckets * FineBucketItems);

            scratchIndicesCounts.Clear();

            int foundSolutions = 0;

            for (byte bucketIdx = 0; bucketIdx < (256 / 2 + 1); ++bucketIdx)
            {
                int cplBucket = -bucketIdx & (NumCoarseBuckets - 1);
                bool nodup = cplBucket == bucketIdx;

                scratchIndicesCounts.Clear();

                uint cplBuckSize = stage3IndicesCounts[cplBucket];

                var stage3Index = cplBucket * CoarseBucketItems;

                for (ushort itemIdx = 0; itemIdx < cplBuckSize; ++itemIdx)
                {
                    uint value = stage3Data[stage3Index + itemIdx];
                    var fineBuckIdx = (int)(value & 127);
                    var fineItemIdx = scratchIndicesCounts[fineBuckIdx];

                    if (fineItemIdx >= FineBucketItems)
                    {
                        continue;
                    }

                    scratchIndicesCounts[fineBuckIdx] = (byte)(fineItemIdx + 1);
                    scratchData[fineBuckIdx * FineBucketItems + fineItemIdx] = itemIdx;

                    if (cplBucket == bucketIdx)
                    {
                        MakePairs(solutions, value, scratchData, scratchIndicesCounts, stage3IndicesCounts, stage3IndicesData, stage3Data, itemIdx);

                        if (foundSolutions >= 8)
                        {
                            return foundSolutions;
                        }
                    }
                }

                if (cplBucket != bucketIdx)
                {
                    uint buckSize = stage3IndicesCounts[bucketIdx];

                    for (int itemIdx = 0; itemIdx < buckSize; ++itemIdx)
                    {
                        uint value = stage3Data[bucketIdx * CoarseBucketItems + itemIdx];

                        MakePairs(solutions, value, scratchData, scratchIndicesCounts, stage3IndicesCounts, stage3IndicesData, stage3Data, itemIdx);

                        if (foundSolutions >= 8)
                        {
                            return foundSolutions;
                        }
                    }
                }

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                void MakePairs(Span<EquixSolution> solutions, uint value, Span<ushort> scratchData, Span<byte> scratchIndicesCount, Span<ushort> stage3Size, Span<uint> stage3IdxData, Span<uint> stage3Data, int itemIdx)
                {
                    if (bucketIdx != 0)
                    {
                        value++;
                    }

                    var fineBuckIdx = (int)(value & 127);
                    var fineCplBucket = (int)((-fineBuckIdx) & 127);
                    var fineCplSize = scratchIndicesCount[fineCplBucket];

                    var scratchDataIndex = fineCplBucket * 12;
                    var stage3DataIndex = cplBucket * 336;
                    for (int fineIdx = 0; fineIdx < fineCplSize; ++fineIdx)
                    {
                        var cplIndex = scratchData[scratchDataIndex + fineIdx];
                        uint cplValue = stage3Data[stage3DataIndex + cplIndex];
                        uint sum = value + cplValue;

                        sum /= 128;

                        if (((sum & (1ul << 15) - 1)) == 0)
                        {
                            uint itemLeft = stage3IdxData[bucketIdx * 336 + itemIdx];
                            uint itemRight = stage3IdxData[stage3DataIndex + cplIndex];

                            BuildSolution(solutions.Slice(foundSolutions, 1), itemLeft, itemRight);

                            if ((++foundSolutions) >= 8)
                            {
                                return;
                            }
                        }
                    }
                }
            }

            return foundSolutions;
        }

        private unsafe void BuildSolutionStage1(Span<ushort> solution, uint root)
        {
            int bucket = (int)(root & 255);
            int bucketInv = -bucket & 255;
            int leftParentIdx = (int)(root >> 17);
            int rightParentIdx = (int)(root >> 8) & 511;

            void* h = Heap.ToPointer();

            Span<ushort> stage1IndicesData = new Span<ushort>(((byte*)h + 512), NumCoarseBuckets * CoarseBucketItems);

            ushort leftParent = stage1IndicesData[bucket * CoarseBucketItems + leftParentIdx];
            ushort rightParent = stage1IndicesData[bucketInv * CoarseBucketItems + rightParentIdx];

            solution[0] = leftParent;
            solution[1] = rightParent;
        }

        private unsafe void BuildSolutionStage2(Span<ushort> solution, uint root)
        {
            int bucket = (int)(root & 255);
            int bucketInv = -bucket & 255;
            int leftParentIdx = (int)(root >> 17);
            int rightParentIdx = (int)(root >> 8) & 511;

            void* h = Heap.ToPointer();
            Span<uint> stage2IndicesData = new Span<uint>(((byte*)h + 173056), NumCoarseBuckets * CoarseBucketItems);

            uint leftParent = stage2IndicesData[bucket * CoarseBucketItems + leftParentIdx];
            uint rightParent = stage2IndicesData[bucketInv * CoarseBucketItems + rightParentIdx];

            BuildSolutionStage1(solution, leftParent);
            BuildSolutionStage1(solution.Slice(2), rightParent);
        }

        private void BuildSolution(Span<EquixSolution> solution, uint itemLeft, uint itemRight)
        {
            var ushortSolution = MemoryMarshal.Cast<EquixSolution, ushort>(solution);

            BuildSolutionStage2(ushortSolution, itemLeft);
            BuildSolutionStage2(ushortSolution.Slice(4), itemRight);
        }

        public unsafe int Solve(HashX program, Span<EquixSolution> solutions, nint regs)
        {
            SolveStage0(program, (ulong*)regs);
            SolveStage1();
            SolveStage2();

            return SolveStage3(solutions);
        }

        //Regs must be at least 36 ulong values. 4 for output, 4*8 for registers
        public unsafe int Solve_Avx2(HashX program, Span<EquixSolution> solutions, nint regs)
        {
            SolveStage0_Avx2(program, (ulong*)regs);
            SolveStage1();
            SolveStage2();
            return SolveStage3(solutions);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public unsafe int Solve_C(HashX program, EquixSolution* solutions, nint heap, nint computeSolutions)
        {
            ulong* regs = (ulong*)computeSolutions + ushort.MaxValue + 1;

            for (int i = 0; i <= ushort.MaxValue; i ++)
            {
                ((ulong*)computeSolutions)[i] = program.AsmCompiled((ulong)i, regs);
            }

            return C_Solve((ulong*)computeSolutions, (byte*)heap, solutions);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public unsafe int Solve_FullC(HashX program, EquixSolution* solutions, nint heap, nint computeSolutions)
        {
            ulong* regs = (ulong*)computeSolutions;


            regs[0] = program.RegisterKey.V0;
            regs[1] = program.RegisterKey.V1;
            regs[2] = program.RegisterKey.V2;
            regs[3] = program.RegisterKey.V3;


            return C_Solve_Full(program._compiledFunction, (byte*)heap, solutions, regs);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public unsafe int Solve_FullC_Avx2(HashX program, EquixSolution* solutions, nint heap, nint computeSolutions)
        {
            ulong* regs = (ulong*)computeSolutions;

            regs[0] = program.RegisterKey.V0;
            regs[1] = program.RegisterKey.V1;
            regs[2] = program.RegisterKey.V2;
            regs[3] = program.RegisterKey.V3;

            return C_Solve_Full_Avx2(program._compiledFunction, (byte*)heap, solutions, regs);
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        //public unsafe int Solve_FullC_Avx512(HashX program, EquixSolution* solutions, nint heap, nint computeSolutions)
        //{
        //    ulong* regs = (ulong*)computeSolutions;

        //    regs[0] = program.RegisterKey.V0;
        //    regs[1] = program.RegisterKey.V1;
        //    regs[2] = program.RegisterKey.V2;
        //    regs[3] = program.RegisterKey.V3;

        //    return C_Solve_Full_Avx512(program._compiledFunction, (byte*)heap, solutions, regs);
        //}

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public unsafe int Solve_Avx2_C(HashX program, EquixSolution* solutions, nint heap, nint computeSolutions)
        {
            for (int i = 0; i <= ushort.MaxValue; i += 4)
            {
                program.AsmCompiled_Avx2((ulong)i, (ulong*)computeSolutions + i);
            }

            return C_Solve((ulong*)computeSolutions, (byte*)heap, solutions);
        }

        //Regs must be at least 72 ulong values. 8 for output, 8*8 for registers
        public unsafe int Solve_Avx512_C(HashX program, EquixSolution* solutions, nint heap, nint computeSolutions)
        {
            //for (int i = 0; i <= ushort.MaxValue; i += 8)
            //{
                program.AsmCompiled_Avx512((ulong)0, (ulong*)computeSolutions);
            //}

            return Solve_Opt((ulong*)computeSolutions, (byte*)heap, solutions);
        }

        public EquixResult Verify(HashX program, EquixSolution solutions)
        {
            ulong* regs = stackalloc ulong[8];

            ulong pair0 = SumPair(solutions.V0, solutions.V1);

            if ((pair0 & EquixStage1Mask) != 0)
            {
                return EquixResult.EquixPartialSum;
            }

            ulong pair1 = SumPair(solutions.V2, solutions.V3);

            if ((pair1 & EquixStage1Mask) != 0)
            {
                return EquixResult.EquixPartialSum;
            }

            ulong pair2 = SumPair(solutions.V4, solutions.V5);

            if ((pair2 & EquixStage1Mask) != 0)
            {
                return EquixResult.EquixPartialSum;
            }

            ulong pair3 = SumPair(solutions.V6, solutions.V7);

            if ((pair3 & EquixStage1Mask) != 0)
            {
                return EquixResult.EquixPartialSum;
            }


            ulong pair4 = pair0 + pair1;

            if ((pair4 & EquixStage2Mask) != 0)
            {
                return EquixResult.EquixPartialSum;
            }

            ulong pair5 = pair2 + pair3;

            if ((pair5 & EquixStage2Mask) != 0)
            {
                return EquixResult.EquixPartialSum;
            }

            ulong pair6 = pair4 + pair5;

            if ((pair6 & EquixFullMask) != 0)
            {
                return EquixResult.EquixFinalSum;
            }


            return EquixResult.EquixOk;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            ulong SumPair(ushort a, ushort b)
            {
                var p1 = program.AsmCompiled(a, regs);
                var p2 = program.AsmCompiled(b, regs);

                return p1 + p2;

            }
        }


        #region Optimized

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe int Solve_Opt(ulong* values, byte* heap, EquixSolution* solutions)
        {
            Radix* rs = (Radix*)heap;

            ulong* stageA = values;
            ulong* stageB = rs->Stage1;

            #region Stage 1

            Sort(stageA, rs->Counts, rs->TempCounts, stageB, Radix.Total, 60);
            int stage1Count = CombineStage1(rs->Counts, stageB, stageA, rs->Stage1Indices);

            #region Stage 1 Verification

            ////Stage 1 test
            //var stage1Indices = new Span<ushort>(rs->Stage1Indices, Radix.Total * 2);
            //HashSet<(ushort, ushort)> pairs = new HashSet<(ushort, ushort)>();

            //for (int i = 0; i < stage1Count; i += 2)
            //{
            //    var what = stage1Indices[i];
            //    var what2 = stage1Indices[i + 1];

            //    var a = values[what];
            //    var b = values[what2];

            //    var add = (a + b);

            //    var mask = ((1ul << 45) - 1);
            //    var val = rs->Stage1[i / 2] & mask;

            //    if (((add >> 15 & mask)) != val)
            //    {
            //        Console.WriteLine("Bad - 1");
            //    }
            //    if (!pairs.Add((what, what2)))
            //    {
            //        Console.WriteLine("Dup");
            //    }

            //    if ((add & ((1 << 15) - 1)) != 0)
            //    {
            //        Console.WriteLine("Bad - 2");
            //    }
            //}

            #endregion

            #endregion

            #region Stage 2

            Sort(stageA, rs->Counts, rs->TempCounts, stageB, stage1Count, 45);
            int stage2Count = CombineStage2(rs->Counts, stageB, stageA, rs->Stage2Indices, stage1Count);

            #region Stage 2 Verification

            ////Stage 2 test
            //var stage1Indices_ = new Span<ushort>(rs->Stage1Indices, Radix.Total * 2);
            //var stage2Indices = new Span<ushort>(rs->Stage2Indices, Radix.Total * 2);
            //var stage2Value = new Span<ulong>(rs->Stage2, Radix.Total);
            //HashSet<(ushort, ushort)> pairs2 = new HashSet<(ushort, ushort)>();

            //for (int i = 0; i < stage2Count * 2; i += 2)
            //{
            //    var stage2A = stage2Indices[i];
            //    var stage2B = stage2Indices[i + 1];

            //    var stage1AA = stage1Indices_[stage2A * 2];
            //    var stage1AB = stage1Indices_[stage2A * 2 + 1];

            //    var stage1BA = stage1Indices_[stage2B * 2];
            //    var stage1BB = stage1Indices_[stage2B * 2 + 1];

            //    var stage1A = values[stage1AA] + values[stage1AB];
            //    var stage1B = values[stage1BA] + values[stage1BB];

            //    var ss = stage1A >> 15;
            //    var bb = stage1B >> 15;

            //    var expectedStage1A = rs->Stage1[stage2A];
            //    var expectedStage1B = rs->Stage1[stage2B];

            //    var stage2 = (stage1A + stage1B);

            //    if (!pairs2.Add((stage2A, stage2B)))
            //    {
            //        Console.WriteLine("Dup");
            //    }

            //    var expectedValue = (stage2 & ((1ul << 60) - 1)) >> 30;
            //    var stage2Result = stage2Value[i / 2];

            //    if (expectedValue != (stage2Result & (1ul << 30) - 1))
            //    {
            //        Console.WriteLine("Bad-Result");
            //    }

            //    if ((stage2 & ((1ul << 30) - 1)) != 0)
            //    {
            //        Console.WriteLine("Bad");
            //    }
            //}

            #endregion

            #endregion

            #region Stage 3

            SortFinal(stageA, rs->Counts, rs->TempCounts, stageB, stage2Count, 30);
            int count = CombineStage3(rs, values, solutions, stage2Count);

            #endregion;

            return count;
        }

        //[MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private unsafe void Sort(ulong* values, byte* counts, ushort* tempCounts, ulong* tempStage, int totalNum, int totalMaskBits)
        {
            var cc = new Span<byte>(counts, Radix.Total / 2);
            cc.Fill(0);

            int remC = totalNum & (4 - 1);
            int totalCount = totalNum - remC;

            var end = values + totalCount;


            //Expected: ~15us. Actual: ~17us
            for (ulong* i = values; i < end; i += 4)
            {
                ushort bucket = (ushort)((*i >> 0) & 0x7FFF);
                ushort bucket1 = (ushort)(((*(i + 1)) >> 0) & 0x7FFF);
                ushort bucket2 = (ushort)(((*(i + 2)) >> 0) & 0x7FFF);
                ushort bucket3 = (ushort)(((*(i + 3)) >> 0) & 0x7FFF);

                counts[bucket]++;
                counts[bucket1]++;
                counts[bucket2]++;
                counts[bucket3]++;
            }

            //Handle extra
            for (ulong* i = end; i < (end + remC); i++)
            {
                ushort bucket = (ushort)((*i >> 0) & 0x7FFF);
                counts[bucket]++;
            }

            int prevValue = 0;
            tempCounts[0] = counts[0];

            for (int i = 0; i < Radix.Total / 2; i += 4)
            {
                prevValue += counts[i];
                tempCounts[i] = (ushort)prevValue;

                prevValue += counts[i + 1];
                tempCounts[i + 1] = (ushort)prevValue;

                prevValue += counts[i + 2];
                tempCounts[i + 2] = (ushort)prevValue;

                prevValue += counts[i + 3];
                tempCounts[i + 3] = (ushort)prevValue;
            }

            ulong mask = (1ul << totalMaskBits) - 1;

            //Expected: ~87us. Actual: 87us
            int total = totalNum;
            const int loopUnroll = 4; // Change from 2 to 4

            int rem = totalNum & (loopUnroll - 1);

            total -= rem;

            for (int i = 0; i < total; i += loopUnroll) // Step by 4
            {
                // Process element 0
                var val0 = values[i];
                var bucket0 = val0 & 0x7FFF;
                var loc0 = --tempCounts[bucket0];
                tempStage[loc0] = ((val0 & mask) >> 15) | ((ulong)i << 48);

                // Process element 1
                var val1 = values[i + 1];
                var bucket1 = val1 & 0x7FFF;
                var loc1 = --tempCounts[bucket1];
                tempStage[loc1] = ((val1 & mask) >> 15) | ((ulong)(i + 1) << 48);

                // Process element 2 (New)
                var val2 = values[i + 2];
                var bucket2 = val2 & 0x7FFF;
                var loc2 = --tempCounts[bucket2];
                tempStage[loc2] = ((val2 & mask) >> 15) | ((ulong)(i + 2) << 48);

                // Process element 3 (New)
                var val3 = values[i + 3];
                var bucket3 = val3 & 0x7FFF;
                var loc3 = --tempCounts[bucket3];
                tempStage[loc3] = ((val3 & mask) >> 15) | ((ulong)(i + 3) << 48);
            }


            //Handle any extra
            for (int i = total; i < totalNum; i++)
            {
                var val = values[i];
                var bucket = val & 0x7FFF;

                var loc = --tempCounts[bucket];

                tempStage[loc] = ((val & mask) >> 15) | ((ulong)i << 48);
            }
        }


        //[MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private unsafe void SortFinal(ulong* values, byte* counts, ushort* tempCounts, ulong* tempStage, int totalNum, int totalMaskBits)
        {
            var cc = new Span<byte>(counts, Radix.Total / 2);
            cc.Fill(0);

            int remC = totalNum & (4 - 1);
            int totalCount = totalNum - remC;

            var end = values + totalCount;


            //Expected: ~15us. Actual: ~17us
            for (ulong* i = values; i < end; i += 4)
            {
                ushort bucket = (ushort)((*i >> 0) & 0x7FFF);
                ushort bucket1 = (ushort)(((*(i + 1)) >> 0) & 0x7FFF);
                ushort bucket2 = (ushort)(((*(i + 2)) >> 0) & 0x7FFF);
                ushort bucket3 = (ushort)(((*(i + 3)) >> 0) & 0x7FFF);

                counts[bucket]++;
                counts[bucket1]++;
                counts[bucket2]++;
                counts[bucket3]++;
            }

            //Handle extra
            for (ulong* i = end; i < (end + remC); i++)
            {
                ushort bucket = (ushort)((*i >> 0) & 0x7FFF);
                counts[bucket]++;
            }

            int prevValue = 0;
            tempCounts[0] = counts[0];

            for (int i = 0; i < Radix.Total / 2; i += 4)
            {
                prevValue += counts[i];
                tempCounts[i] = (ushort)prevValue;

                prevValue += counts[i + 1];
                tempCounts[i + 1] = (ushort)prevValue;

                prevValue += counts[i + 2];
                tempCounts[i + 2] = (ushort)prevValue;

                prevValue += counts[i + 3];
                tempCounts[i + 3] = (ushort)prevValue;
            }

            ulong mask = (1ul << totalMaskBits) - 1;

            //Expected: ~87us. Actual: 87us
            int total = totalNum;
            const int loopUnroll = 2;

            int rem = totalNum & (loopUnroll - 1);

            total -= rem;

            for (int i = 0; i < total; i += loopUnroll)
            {
                var val = values[i] >> 0;
                var val2 = values[i + 1] >> 0;
                var bucket = val & 0x7FFF;
                var bucket2 = val2 & 0x7FFF;

                var loc = --tempCounts[bucket];
                var loc2 = --tempCounts[bucket2];

                tempStage[loc] = ((values[i] & mask)) | ((ulong)i << 48);
                tempStage[loc2] = ((values[i + 1] & mask)) | ((ulong)(i + 1) << 48);
            }


            //Handle any extra
            for (int i = total; i < totalNum; i++)
            {
                var val = values[i];
                var bucket = val & 0x7FFF;

                var loc = --tempCounts[bucket];

                tempStage[loc] = ((val & mask)) | ((ulong)i << 48);
            }
        }

        //[MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]

        private unsafe int CombineStage1(byte* counts, ulong* tempStage, ulong* valOutput, uint* indices)
        {
            int index = 0;
            uint val0Index = counts[0];
            uint val1Index = Radix.Total;

            for (int bucketIdx = 1; bucketIdx < Radix.Total / 4; bucketIdx++)
            {
                int inverse = -bucketIdx & 0x7FFF;

                var curVal0Count = counts[bucketIdx];
                var curVal1Count = counts[inverse];


                if (curVal0Count == 0 || curVal1Count == 0)
                {
                    val0Index += curVal0Count;
                    val1Index -= curVal1Count;
                    continue;
                }

                var indexIncrease = Math.Min((byte)8, curVal1Count);

                if (Avx512F.IsSupported)
                {
                    Vector512<ulong> valLoad;
                    ulong* valCreate;
                    byte valCount = 0;

                    //if (curVal1Count < curVal0Count)
                    //{
                    //    indexIncrease = Math.Min((byte)8, curVal0Count);
                    //    valLoad = Avx512F.LoadVector512(tempStage + val0Index);
                    //    valCreate = tempStage + val1Index - curVal1Count;
                    //    valCount = curVal1Count;
                    //}
                    //else
                    {
                        valLoad = Avx512F.LoadVector512(tempStage + val1Index - curVal1Count);
                        valCreate = tempStage + val0Index;
                        valCount = curVal0Count;
                    }


                    var index1 = Avx512F.ShiftRightLogical(valLoad, 48);
                    index1 = Avx512F.ShiftLeftLogical(index1, 32);

                    for (int x = 0; x < valCount; x++)
                    {
                        var val0 = Vector512.Create(valCreate[x]);

                        var sumValue = Avx512F.Add(val0, valLoad);
                        sumValue = Avx512F.Add(sumValue, Vector512<ulong>.One);
                        var index0 = Avx512F.ShiftRightLogical(val0, 48);

                        var fullIndexes = Avx512F.Xor(index0, index1);

                        Avx512F.Store(valOutput + index, sumValue);
                        Avx.Store((byte*)(indices + index), Avx512F.ConvertToVector256UInt16(fullIndexes.AsUInt32()).AsByte());

                        index += indexIncrease;

                        if (index >= Radix.Total)
                        {
                            goto end;
                        }
                    }
                }
                else if (Avx2.IsSupported)
                {
                    for (int x = 0; x < curVal0Count; x++)
                    {
                        var val0 = Vector256.Create(tempStage[val0Index + x]);
                        var val1 = Avx.LoadVector256(tempStage + val1Index - curVal1Count);

                        var sumValue = Avx2.Add(val0, val1);
                        var index0 = Avx2.ShiftRightLogical(val0, 48);
                        sumValue = Avx2.Add(sumValue, Vector256<ulong>.One);
                        var index1 = Avx2.ShiftLeftLogical(Avx2.ShiftRightLogical(val1, 48), 32);
                        var fullIndexes = Avx2.Xor(index0, index1);


                        Avx.Store(valOutput + index, sumValue);
                        Avx.Store((byte*)(indices + index), Vector256.Narrow(fullIndexes.AsUInt32(), fullIndexes.AsUInt32()).AsByte());

                        if (indexIncrease >= 4)
                        {
                            var val1_ = Avx.LoadVector256(tempStage + val1Index - curVal1Count + 4);
                            var sumValue_ = Avx2.Add(val0, val1_);
                            var index1_ = Avx2.ShiftLeftLogical(Avx2.ShiftRightLogical(val1_, 48), 32);
                            sumValue_ = Avx2.Add(sumValue_, Vector256<ulong>.One);
                            var fullIndexes_ = Avx2.Xor(index0, index1_);
                            Avx.Store(valOutput + index + 4, sumValue_);
                            Avx.Store((byte*)(indices + index + 4), Vector256.Narrow(fullIndexes_.AsUInt32(), fullIndexes_.AsUInt32()).AsByte());
                        }

                        index += indexIncrease;

                        if (index >= Radix.Total)
                        {
                            goto end;
                        }
                    }
                }

                val0Index += curVal0Count;
                val1Index -= curVal1Count;

            }

        end:

            return index;
        }

        private unsafe int CombineStage2(byte* counts, ulong* tempStage, ulong* valOutput, uint* indices, int totalNum)
        {
            ////Expected: ~151us (clang) / 164 (msvc). Actual: 125us
            int index = 0;
            uint val0Index = counts[0];
            uint val1Index = (uint)totalNum;
            var carry = Vector512<ulong>.One;

            for (int bucketIdx = 1; bucketIdx < Radix.Total / 4; bucketIdx++)
            {
                int inverse = -bucketIdx & 0x7FFF;

                var curVal0Count = counts[bucketIdx];
                var curVal1Count = counts[inverse];


                if (curVal0Count == 0 || curVal1Count == 0)
                {
                    val0Index += curVal0Count;
                    val1Index -= curVal1Count;
                    continue;
                }

                var indexIncrease = Math.Min((byte)8, curVal1Count);

                if (Avx512F.IsSupported)
                {
                    Vector512<ulong> valLoad;
                    ulong* valCreate;
                    byte valCount = 0;

                    //if (curVal1Count < curVal0Count)
                    //{
                    //    indexIncrease = Math.Min((byte)8, curVal0Count);
                    //    valLoad = Avx512F.LoadVector512(tempStage + val0Index);
                    //    valCreate = tempStage + val1Index - curVal1Count;
                    //    valCount = curVal1Count;
                    //}
                    //else
                    {
                        valLoad = Avx512F.LoadVector512(tempStage + val1Index - curVal1Count);
                        valCreate = tempStage + val0Index;
                        valCount = curVal0Count;
                    }


                    var index1 = Avx512F.ShiftRightLogical(valLoad, 48);
                    index1 = Avx512F.ShiftLeftLogical(index1, 32);

                    for (int x = 0; x < valCount; x++)
                    {
                        var val0 = Vector512.Create(valCreate[x]);

                        var sumValue = Avx512F.Add(val0, valLoad);
                        sumValue = Avx512F.Add(sumValue, carry);
                        var index0 = Avx512F.ShiftRightLogical(val0, 48);

                        var fullIndexes = Avx512F.Xor(index0, index1);

                        Avx512F.Store(valOutput + index, sumValue);
                        Avx.Store((byte*)(indices + index), Avx512F.ConvertToVector256UInt16(fullIndexes.AsUInt32()).AsByte());

                        index += indexIncrease;

                        if (index >= Radix.Total)
                        {
                            goto end;
                        }
                    }
                }
                else if (Avx2.IsSupported)
                {
                    for (int x = 0; x < curVal0Count; x++)
                    {
                        var val0 = Vector256.Create(tempStage[val0Index + x]);
                        var val1 = Avx.LoadVector256(tempStage + val1Index - curVal1Count);

                        var sumValue = Avx2.Add(val0, val1);
                        var index0 = Avx2.ShiftRightLogical(val0, 48);
                        sumValue = Avx2.Add(sumValue, Vector256<ulong>.One);
                        var index1 = Avx2.ShiftLeftLogical(Avx2.ShiftRightLogical(val1, 48), 32);
                        var fullIndexes = Avx2.Xor(index0, index1);

                        var val1_ = Avx.LoadVector256(tempStage + val1Index - curVal1Count + 4);
                        var sumValue_ = Avx2.Add(val0, val1_);
                        var index1_ = Avx2.ShiftLeftLogical(Avx2.ShiftRightLogical(val1_, 48), 32);
                        sumValue_ = Avx2.Add(sumValue_, Vector256<ulong>.One);
                        var fullIndexes_ = Avx2.Xor(index0, index1_);

                        Avx.Store(valOutput + index, sumValue);
                        Avx.Store((byte*)(indices + index), Vector256.Narrow(fullIndexes.AsUInt32(), fullIndexes.AsUInt32()).AsByte());

                        if (indexIncrease >= 4)
                        {
                            Avx.Store(valOutput + index + 4, sumValue_);
                            Avx.Store((byte*)(indices + index + 4), Vector256.Narrow(fullIndexes_.AsUInt32(), fullIndexes_.AsUInt32()).AsByte());
                        }

                        index += indexIncrease;

                        if (index >= Radix.Total)
                        {
                            goto end;
                        }
                    }
                }

                val0Index += curVal0Count;
                val1Index -= curVal1Count;

            }

        end:

            return index;
        }
        private unsafe int CombineStage3(Radix* rs, ulong* values, EquixSolution* solutions, int totalNum)
        {
            var counts = rs->Counts;
            var tempCounts = rs->TempCounts;
            var tempStage = rs->Stage1;

            int index = 0;
            uint val0Index = counts[0];
            uint val1Index = (uint)totalNum - 1;
            var carry = Vector512<ulong>.One;
            int totalSolutions = 0;

            for (int bucketIdx = 1; bucketIdx < Radix.Total / 4; bucketIdx++)
            {
                int inverse = -bucketIdx & 0x7FFF;

                var curVal0Count = counts[bucketIdx];
                var curVal1Count = counts[inverse];

                var indexIncrease = Math.Min((byte)8, curVal1Count);

                if (curVal0Count == 0 || curVal1Count == 0)
                {
                    val0Index += curVal0Count;
                    val1Index -= curVal1Count;
                    continue;
                }

                if (Avx512F.IsSupported)
                {
                    var val1 = Avx512F.LoadVector512(tempStage + val1Index - curVal1Count + 1);

                    for (int x = 0; x < curVal0Count; x++)
                    {
                        var val0 = Vector512.Create(tempStage[val0Index + x]);

                        var sumValue = Avx512F.Add(val0, val1);
                        //var sumValue2 = Avx512F.Add(sumValue, carry);
                        var test = Avx512F.And(sumValue, Vector512.Create((1ul << 30) - 1));

                        var test2 = Avx512F.CompareEqual(test, Vector512<ulong>.Zero);

                        if (test2 != Vector512<ulong>.Zero)
                        {
                            var index0 = Avx512F.ShiftRightLogical(val0, 48);
                            var index1 = Avx512F.ShiftRightLogical(val1, 48);

                            var stage3IndiceA = index0[0];

                            //Find a faster way
                            for (int z = 0; z < indexIncrease; z++)
                            {
                                if (test2[z] != 0)
                                {
                                    var stage1Indices = rs->Stage1Indices;
                                    var stage2Indices = rs->Stage2Indices;

                                    var stage3IndiceB = index1[z];

                                    var stage2IndicePairA = stage2Indices[stage3IndiceA];
                                    var stage2IndicePairB = stage2Indices[stage3IndiceB];

                                    var stage2IndicePairA_0 = stage2IndicePairA & 0xFFFF;
                                    var stage2IndicePairA_1 = stage2IndicePairA >> 16;

                                    var stage2IndicePairB_0 = stage2IndicePairB & 0xFFFF;
                                    var stage2IndicePairB_1 = stage2IndicePairB >> 16;

                                    var stage1IndicePairA = stage1Indices[stage2IndicePairA_0];
                                    var stage1IndicePairB = stage1Indices[stage2IndicePairA_1];
                                    var stage1IndicePairC = stage1Indices[stage2IndicePairB_0];
                                    var stage1IndicePairD = stage1Indices[stage2IndicePairB_1];


                                    var stage1IndicePairA_0 = stage1IndicePairA & 0xFFFF;
                                    var stage1IndicePairA_1 = stage1IndicePairA >> 16;

                                    var stage1IndicePairB_0 = stage1IndicePairB & 0xFFFF;
                                    var stage1IndicePairB_1 = stage1IndicePairB >> 16;

                                    var stage1IndicePairC_0 = stage1IndicePairC & 0xFFFF;
                                    var stage1IndicePairC_1 = stage1IndicePairC >> 16;

                                    var stage1IndicePairD_0 = stage1IndicePairD & 0xFFFF;
                                    var stage1IndicePairD_1 = stage1IndicePairD >> 16;


                                    var valA_0 = values[stage1IndicePairA_0];
                                    var valA_1 = values[stage1IndicePairA_1];

                                    var valB_0 = values[stage1IndicePairB_0];
                                    var valB_1 = values[stage1IndicePairB_1];

                                    var valC_0 = values[stage1IndicePairC_0];
                                    var valC_1 = values[stage1IndicePairC_1];

                                    var valD_0 = values[stage1IndicePairD_0];
                                    var valD_1 = values[stage1IndicePairD_1];

                                    solutions[totalSolutions++] = new EquixSolution
                                    {
                                        V0 = (ushort)stage1IndicePairA_0,
                                        V1 = (ushort)stage1IndicePairA_1,
                                        V2 = (ushort)stage1IndicePairB_0,
                                        V3 = (ushort)stage1IndicePairB_1,
                                        V4 = (ushort)stage1IndicePairC_0,
                                        V5 = (ushort)stage1IndicePairC_1,
                                        V6 = (ushort)stage1IndicePairD_0,
                                        V7 = (ushort)stage1IndicePairD_1
                                    };

                                    if (totalSolutions >= 8)
                                    {
                                        goto end;
                                    }
                                    //Console.WriteLine($"{stage1IndicePairA_0}, {stage1IndicePairA_1}, {stage1IndicePairB_0}, {stage1IndicePairB_1}, {stage1IndicePairC_0}, {stage1IndicePairC_1}, {stage1IndicePairD_0}, {stage1IndicePairD_1}");
                                }
                            }
                        }


                        //var index1 = Avx512F.ShiftRightLogical(val1, 48);
                        //sumValue = Avx512F.Add(sumValue, carry);
                        //c

                        //index1 = Avx512F.ShiftLeftLogical(index1, 32);
                        //var fullIndexes = Avx512F.Xor(index0, index1);

                        //Avx512F.Store(valOutput + index, sumValue);
                        //Avx.Store((byte*)(indices + index), Avx512F.ConvertToVector256UInt16(fullIndexes.AsUInt32()).AsByte());

                        index += indexIncrease;

                        if (index >= Radix.Total)
                        {
                            goto end;
                        }
                    }
                }
                else if (Avx2.IsSupported)
                {
                    for (int x = 0; x < curVal0Count; x++)
                    {
                        var val0 = Vector256.Create(tempStage[val0Index + x]);

                        for (int y = 0; y < indexIncrease; y += 4)
                        {
                            var val1 = Avx2.LoadVector256(tempStage + val1Index - curVal1Count + 1 + y);

                            var sumValue = Avx512F.Add(val0, val1);
                            //var sumValue2 = Avx512F.Add(sumValue, carry);
                            var test = Avx2.And(sumValue, Vector256.Create((1ul << 30) - 1));

                            var test2 = Avx2.CompareEqual(test, Vector256<ulong>.Zero);

                            if (test2 != Vector256<ulong>.Zero)
                            {
                                var index0 = Avx2.ShiftRightLogical(val0, 48);
                                var index1 = Avx2.ShiftRightLogical(val1, 48);

                                var stage3IndiceA = index0[0];

                                int totalEntries = Math.Min(4, indexIncrease - y);

                                //Find a faster way
                                for (int z = 0; z < totalEntries; z++)
                                {
                                    if (test2[z] != 0)
                                    {
                                        var stage1Indices = rs->Stage1Indices;
                                        var stage2Indices = rs->Stage2Indices;

                                        var stage3IndiceB = index1[z];

                                        var stage2IndicePairA = stage2Indices[stage3IndiceA];
                                        var stage2IndicePairB = stage2Indices[stage3IndiceB];

                                        var stage2IndicePairA_0 = stage2IndicePairA & 0xFFFF;
                                        var stage2IndicePairA_1 = stage2IndicePairA >> 16;

                                        var stage2IndicePairB_0 = stage2IndicePairB & 0xFFFF;
                                        var stage2IndicePairB_1 = stage2IndicePairB >> 16;

                                        var stage1IndicePairA = stage1Indices[stage2IndicePairA_0];
                                        var stage1IndicePairB = stage1Indices[stage2IndicePairA_1];
                                        var stage1IndicePairC = stage1Indices[stage2IndicePairB_0];
                                        var stage1IndicePairD = stage1Indices[stage2IndicePairB_1];


                                        var stage1IndicePairA_0 = stage1IndicePairA & 0xFFFF;
                                        var stage1IndicePairA_1 = stage1IndicePairA >> 16;

                                        var stage1IndicePairB_0 = stage1IndicePairB & 0xFFFF;
                                        var stage1IndicePairB_1 = stage1IndicePairB >> 16;

                                        var stage1IndicePairC_0 = stage1IndicePairC & 0xFFFF;
                                        var stage1IndicePairC_1 = stage1IndicePairC >> 16;

                                        var stage1IndicePairD_0 = stage1IndicePairD & 0xFFFF;
                                        var stage1IndicePairD_1 = stage1IndicePairD >> 16;


                                        var valA_0 = values[stage1IndicePairA_0];
                                        var valA_1 = values[stage1IndicePairA_1];

                                        var valB_0 = values[stage1IndicePairB_0];
                                        var valB_1 = values[stage1IndicePairB_1];

                                        var valC_0 = values[stage1IndicePairC_0];
                                        var valC_1 = values[stage1IndicePairC_1];

                                        var valD_0 = values[stage1IndicePairD_0];
                                        var valD_1 = values[stage1IndicePairD_1];

                                        solutions[totalSolutions++] = new EquixSolution
                                        {
                                            V0 = (ushort)stage1IndicePairA_0,
                                            V1 = (ushort)stage1IndicePairA_1,
                                            V2 = (ushort)stage1IndicePairB_0,
                                            V3 = (ushort)stage1IndicePairB_1,
                                            V4 = (ushort)stage1IndicePairC_0,
                                            V5 = (ushort)stage1IndicePairC_1,
                                            V6 = (ushort)stage1IndicePairD_0,
                                            V7 = (ushort)stage1IndicePairD_1
                                        };

                                        if (totalSolutions >= 8)
                                        {
                                            goto end;
                                        }
                                        //Console.WriteLine($"{stage1IndicePairA_0}, {stage1IndicePairA_1}, {stage1IndicePairB_0}, {stage1IndicePairB_1}, {stage1IndicePairC_0}, {stage1IndicePairC_1}, {stage1IndicePairD_0}, {stage1IndicePairD_1}");
                                    }
                                }
                            }


                            //var index1 = Avx512F.ShiftRightLogical(val1, 48);
                            //sumValue = Avx512F.Add(sumValue, carry);
                            //c

                            //index1 = Avx512F.ShiftLeftLogical(index1, 32);
                            //var fullIndexes = Avx512F.Xor(index0, index1);

                            //Avx512F.Store(valOutput + index, sumValue);
                            //Avx.Store((byte*)(indices + index), Avx512F.ConvertToVector256UInt16(fullIndexes.AsUInt32()).AsByte());

                            index += indexIncrease;

                            if (index >= Radix.Total)
                            {
                                goto end;
                            }
                        }
                    }
                }

                val0Index += curVal0Count;
                val1Index -= curVal1Count;

            }

        end:

            return totalSolutions;
        }

        #endregion

        public void Dispose()
        {
            VirtualMemory.HashxVmFree(CompiledProgram.ToPointer(), X86Compiler.CodeSize);
            NativeMemory.AlignedFree((void*)Heap);
            NativeMemory.AlignedFree((void*)ComputeSolutions);
        }
    }

    public struct EquixSolution
    {
        public const int MaxLength = 8;
        public const int Size = 8 * sizeof(ushort);

        public ushort V0;
        public ushort V1;
        public ushort V2;
        public ushort V3;
        public ushort V4;
        public ushort V5;
        public ushort V6;
        public ushort V7;

        public override string ToString()
        {
            return $"{V0}, {V1}, {V2}, {V3}, {V4}, {V5}, {V6}, {V7}";
        }
    }
}
