using DrillX.Compiler;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
            Heap = (nint)NativeMemory.AlignedAlloc(TotalSize, 4);
            ComputeSolutions = (nint)NativeMemory.AlignedAlloc(MaxComputeSolutionsSize, 4);
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
            for (int i = 0; i <= ushort.MaxValue; i += 8)
            {
                program.AsmCompiled_Avx512((ulong)i, (ulong*)computeSolutions + i);
            }

            return C_Solve((ulong*)computeSolutions, (byte*)heap, solutions);
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
