using DrillX.Compiler;
using DrillX;
using DrillX.Solver;
using ILGPU;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ILGPU.Backends.PTX;
using ILGPU.Backends;
using ILGPU.IR.Intrinsics;
using ILGPU.IR;
using System.Runtime.CompilerServices;

namespace OrionClientLib.Hashers.GPU.RTX4090Opt
{
    public partial class Cuda4090OptGPUHasher
    {
        public const int BlockSize = 128;
        public const int TotalValues = ushort.MaxValue + 1;


        private const int FineBucketItems = 12;
        private const int NumFineBuckets = 128;
        private const int CoarseBucketItems = 336;
        private const int NumCoarseBuckets = 256;

        private const int IndexSpace = 1 << 16;
        private const int CacheSize = 32; //ulong values
        private const int Cache2Size = 4; //ulong values
        private const int MaxSharedSize = 49152;
        //public const int MaxConcurrentGroups = 160; //Limited by shared memory
        public const int HeapDelegateSize = 1024;

        #region Heap Data

        private const int Stage1DataLength = NumCoarseBuckets * CoarseBucketItems * sizeof(ulong);
        private const int Stage1DataOffset = 0;

        private const int Stage1IndiceDataLength = NumCoarseBuckets * CoarseBucketItems * sizeof(ushort);
        private const int Stage1IndiceDataOffset = Stage1DataOffset + Stage1DataLength;

        private const int Stage2DataLength = NumCoarseBuckets * CoarseBucketItems * sizeof(ulong);
        private const int Stage2DataOffset = Stage1IndiceDataOffset + Stage1IndiceDataLength;

        private const int Stage3DataLength = NumCoarseBuckets * CoarseBucketItems * sizeof(ulong);
        private const int Stage3DataOffset = Stage2DataOffset + Stage2DataLength;

        private const int ScratchDataLength = NumFineBuckets * FineBucketItems * sizeof(ushort);
        private const int ScratchDataOffset = Stage3DataOffset + Stage3DataLength;

        public const long HeapSize = Stage1DataLength + Stage1IndiceDataLength + Stage2DataLength + Stage3DataLength + ScratchDataLength;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ArrayView<ulong> Stage1Data(ArrayView<byte> heap) => heap.SubView(Stage1DataOffset, Stage1DataLength).Cast<ulong>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ArrayView<ushort> Stage1IndiceData(ArrayView<byte> heap) => heap.SubView(Stage1IndiceDataOffset, Stage1IndiceDataLength).Cast<ushort>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ArrayView<ulong> Stage2Data(ArrayView<byte> heap) => heap.SubView(Stage2DataOffset, Stage2DataLength).Cast<ulong>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ArrayView<ulong> Stage3Data(ArrayView<byte> heap) => heap.SubView(Stage3DataOffset, Stage3DataLength).Cast<ulong>();

        #endregion

        #region Intrinsics

        [IntrinsicMethod(nameof(ClearIndex_Generate))]
        [IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe static void ClearIndex(ref int indices, int index)
        {
            fixed (int* m = &indices)
            {
                for (int i = index; i < NumCoarseBuckets; i += BlockSize)
                {
                    *(m + i) = 0;
                }
            }
        }

        private static void ClearIndex_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var memoryRef = (RegisterAllocator<PTXRegisterKind>.HardwareRegister)codeGenerator.LoadPrimitive(value[0]);
            var index = (RegisterAllocator<PTXRegisterKind>.HardwareRegister)codeGenerator.LoadPrimitive(value[1]);
            var temp = codeGenerator.AllocateRegister(BasicValueType.Int64, PTXRegisterKind.Int64);

            codeGenerator.BeginCommand($"mul.wide.u32 {temp.GetString()}, {index.GetString()}, 8").Dispose();
            codeGenerator.BeginCommand($"add.u64 {temp.GetString()}, {memoryRef.GetString()}, {temp.GetString()}").Dispose();
            codeGenerator.BeginCommand($"st.shared.v2.b32 [{temp.GetString()}], {{ 0, 0 }}").Dispose();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe static void ClearMemory(ref ulong memory)
        {
            fixed (ulong* m = &memory)
            {
                for (int i = 0; i < NumFineBuckets; i++)
                {
                    *(m + i) = 0;
                }
            }
        }

        [IntrinsicMethod(nameof(ClearScratchIndices_Generate))]
        [IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe static void ClearScratchIndices(ref ulong memory)
        {
            fixed (ulong* m = &memory)
            {
                *(m + 0) = 0;
                *(m + 1) = 0;
                *(m + 2) = 0;
                *(m + 3) = 0;
                *(m + 4) = 0;
                *(m + 5) = 0;
                *(m + 6) = 0;
                *(m + 7) = 0;
            }
        }

        private static void ClearScratchIndices_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var memoryRef = (RegisterAllocator<PTXRegisterKind>.HardwareRegister)codeGenerator.LoadPrimitive(value[0]);

            for (int i = 0; i < 4; i++)
            {
                codeGenerator.BeginCommand($"st.shared.v2.b64 [{memoryRef.GetString()} + {i * 16}], {{0, 0}}").Dispose();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int InvertBucket(int v)
        {
            return ((-v) & 255);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int InvertScratch(int v)
        {
            return ((-v) & 127);
        }

        [IntrinsicMethod(nameof(LoadToSharedU8_Generate))]
        [IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe static void LoadToSharedU8(ref ulong shared, ref ulong global)
        {
            fixed (ulong* s = &shared)
            fixed (ulong* g = &global)
            {
                for (int i = 0; i < CacheSize; i++)
                {
                    *(s + i) = *(g + i);
                }
            }
        }

        private static void LoadToSharedU8_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var sharedRef = (RegisterAllocator<PTXRegisterKind>.HardwareRegister)codeGenerator.LoadPrimitive(value[0]);
            var globalRef = (RegisterAllocator<PTXRegisterKind>.HardwareRegister)codeGenerator.LoadPrimitive(value[1]);
            var r0 = codeGenerator.AllocateRegister(BasicValueType.Int64, PTXRegisterKind.Int64);
            var r1 = codeGenerator.AllocateRegister(BasicValueType.Int64, PTXRegisterKind.Int64);


            if (backend.Architecture.Major < 8)
            {
                for (int i = 0; i < CacheSize / 2; i++)
                {
                    codeGenerator.BeginCommand($"ld.global.v2.b64 {{{r0.GetString()}, {r1.GetString()}}}, [{globalRef.GetString()} + {i * 16}]").Dispose();
                    codeGenerator.BeginCommand($"st.shared.v2.b64 [{sharedRef.GetString()} + {i * 16}], {{{r0.GetString()}, {r1.GetString()}}}").Dispose();
                }
            }
            else
            {
                for (int i = 0; i < CacheSize / 2; i++)
                {
                    codeGenerator.BeginCommand($"cp.async.ca.shared.global [{sharedRef.GetString()} + {i * 16}], [{globalRef.GetString()} + {i * 16}], 16").Dispose();
                }

                codeGenerator.BeginCommand($"cp.async.wait_all").Dispose();
            }
        }

        [IntrinsicMethod(nameof(LoadToSharedU4_Generate))]
        [IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe static void LoadToSharedU4(ref uint shared, ref uint global)
        {
            fixed (uint* s = &shared)
            fixed (uint* g = &global)
            {
                for (int i = 0; i < CacheSize * 2; i++)
                {
                    *(s + i) = *(g + i);
                }
            }
        }

        private static void LoadToSharedU4_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var sharedRef = (RegisterAllocator<PTXRegisterKind>.HardwareRegister)codeGenerator.LoadPrimitive(value[0]);
            var globalRef = (RegisterAllocator<PTXRegisterKind>.HardwareRegister)codeGenerator.LoadPrimitive(value[1]);
            var r0 = codeGenerator.AllocateRegister(BasicValueType.Int64, PTXRegisterKind.Int64);
            var r1 = codeGenerator.AllocateRegister(BasicValueType.Int64, PTXRegisterKind.Int64);

            if (backend.Architecture.Major < 8)
            {
                for (int i = 0; i < CacheSize / 2; i++)
                {
                    codeGenerator.BeginCommand($"ld.global.v2.b64 {{{r0.GetString()}, {r1.GetString()}}}, [{globalRef.GetString()} + {i * 16}]").Dispose();
                    codeGenerator.BeginCommand($"st.shared.v2.b64 [{sharedRef.GetString()} + {i * 16}], {{{r0.GetString()}, {r1.GetString()}}}").Dispose();
                }
            }
            else
            {
                for (int i = 0; i < CacheSize / 2; i++)
                {
                    codeGenerator.BeginCommand($"cp.async.ca.shared.global [{sharedRef.GetString()} + {i * 16}], [{globalRef.GetString()} + {i * 16}], 16").Dispose();
                }

                codeGenerator.BeginCommand($"cp.async.wait_all").Dispose();
            }
        }

        [IntrinsicMethod(nameof(LoadStageData_Generate))]
        [IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe static void LoadStageData(ref ulong shared, ref ulong global, ulong u0, ulong u1, ulong u2, ulong u3)
        {
            fixed (ulong* s = &shared)
            fixed (ulong* g = &global)
            {
                *(s + 0) = *(g + u0);
                *(s + 1) = *(g + u1);
                *(s + 2) = *(g + u2);
                *(s + 3) = *(g + u3);
            }
        }

        private static void LoadStageData_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var sharedRef = (RegisterAllocator<PTXRegisterKind>.HardwareRegister)codeGenerator.LoadPrimitive(value[0]);
            var globalRef = (RegisterAllocator<PTXRegisterKind>.HardwareRegister)codeGenerator.LoadPrimitive(value[1]);
            var r0 = codeGenerator.AllocateRegister(BasicValueType.Int64, PTXRegisterKind.Int64);

            if (backend.Architecture.Major < 8)
            {
                for (int i = 0; i < 4; i++)
                {
                    var index = (RegisterAllocator<PTXRegisterKind>.HardwareRegister)codeGenerator.LoadPrimitive(value[i + 2]);

                    codeGenerator.BeginCommand($"add.u64 {index.GetString()}, {index.GetString()}, {globalRef.GetString()}").Dispose();
                    codeGenerator.BeginCommand($"ld.global.b64 {r0.GetString()}, [{index.GetString()}]").Dispose();
                    codeGenerator.BeginCommand($"st.shared.b64 [{sharedRef.GetString()} + {i + 8}], {r0.GetString()}").Dispose();
                }
            }
            else
            {
                for (int i = 0; i < 4; i++)
                {
                    var index = (RegisterAllocator<PTXRegisterKind>.HardwareRegister)codeGenerator.LoadPrimitive(value[i + 2]);

                    codeGenerator.BeginCommand($"add.u64 {index.GetString()}, {index.GetString()}, {globalRef.GetString()}").Dispose();
                    codeGenerator.BeginCommand($"cp.async.ca.shared.global [{sharedRef.GetString()} + {i * 8}], [{index.GetString()}], 8").Dispose();
                }

                codeGenerator.BeginCommand($"cp.async.wait_all").Dispose();
            }
        }


        private static void Clear(ArrayView<uint> data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = 0;
            }
        }

        #endregion

        private static void Equihash(ArrayView<ulong> values, ArrayView<EquixSolution> solutions, ArrayView<ushort> globalHeap, ArrayView<uint> solutionCount)
        {
            var grid = Grid.GlobalIndex;
            var group = Group.Dimension;

            int blockIndex = (grid.X * group.Y + grid.Y);
            int block = blockIndex / BlockSize;

            values = values.SubView(block * TotalValues, TotalValues);

            //Interop.WriteLine("start {0}", index);
            #region Shared Memory Setup

            const int scratchIndiceLength = NumFineBuckets / 2 * BlockSize; //8KB
            const int indiceALength = NumCoarseBuckets; //1KB
            const int indiceBLength = NumCoarseBuckets; //1KB
            const int cacheLength = CacheSize * BlockSize; //~38KB
           // const int cache2Length = Cache2Size * BlockSize;
            //var sMemory = SharedMemory.Allocate<byte>(MaxSharedSize);

            var scratchIndices = SharedMemory.Allocate<byte>(scratchIndiceLength);// sMemory.SubView(scratchIndiceOffset, scratchIndiceLength).Cast<byte>();// SharedMemory.Allocate<byte>(NumFineBuckets / 2 * BlockSize);
            var indices1 = SharedMemory.Allocate<int>(indiceALength); // sMemory.SubView(indiceAOffset, indiceALength).Cast<int>();// SharedMemory.Allocate<int>(NumCoarseBucket);
            var indices2 = SharedMemory.Allocate<int>(indiceBLength); //sMemory.SubView(indiceBOffset, indiceBLength).Cast<int>(); ;// SharedMemory.Allocate<int>(NumCoarseBucket);
            var cache = SharedMemory.Allocate<short>(cacheLength * 4);  //sMemory.SubView(cacheOffset, cacheLength).Cast<short>();// SharedMemory.Allocate<short>(CacheSize * BlockSize * 4);
            //var cache2 = SharedMemory.Allocate<ulong>(cache2Length);

            #endregion

            var idx = Group.IdxX;

            //int totalGroups = (int)((globalHeap.Length) / HeapSize * 2);

            //var heapCounter = globalHeap.SubView(0, 2).Cast<int>();
            //var heapLocker = globalHeap.SubView(2, 2).Cast<int>();
            //var heapDelegation = globalHeap.SubView(4, totalGroups - 4);

            //int heapLocation;

            if (Group.IsFirstThread)
            {
                //Clear solution

                solutionCount[block] = 0;
            }


            //if (Group.IsFirstThread)
            //{
            //    while (true)
            //    {
            //        if (Atomic.Exchange(ref heapLocker[0], 1) == 0)
            //        {
            //            MemoryFence.SystemLevel();

            //            var delegateLocation = heapCounter[0];
            //            var counter = Atomic.Add(ref heapCounter[0], 1);

            //            heapLocation = heapDelegation[delegateLocation];

            //            heapDelegation[delegateLocation] = 0xFFFF;

            //            //Interop.WriteLine("Start block {0}. Heap Location: {1}. Delegate Location: {2}. New Count: {3}", block, heapLocation, delegateLocation, heapCounter[0]);
            //            Atomic.Exchange(ref heapLocker[0], 0);

            //            break;
            //        }
            //    }


            //    cache[0] = (short)heapLocation;
            //}

            //Group.Barrier();

            //heapLocation = cache[0];

            var heap = globalHeap.Cast<byte>().SubView(HeapSize * block, HeapSize);

            //var scratch = heap.SubView(ScratchDataOffset, ScratchDataLength).Cast<ushort>();

            var scratch = LocalMemory.Allocate<ushort>(ScratchDataLength / 2);

            SolveStage0(values, heap, indices1, idx, cache.Cast<ulong>().SubView(CacheSize * idx, CacheSize));
            SolveStage1(heap, scratch, indices1, indices2, idx, cache.Cast<ulong>().SubView(CacheSize * idx, CacheSize), scratchIndices.SubView((NumFineBuckets / 2) * idx, NumFineBuckets / 2));
            SolveStage2(heap, scratch, indices2, indices1, idx, cache.Cast<ulong>().SubView(CacheSize * idx, CacheSize), scratchIndices.SubView((NumFineBuckets / 2) * idx, NumFineBuckets / 2));
            SolveStage3(heap, scratch, solutions.Cast<ushort>().SubView(block * 64), indices1, idx, cache.Cast<ulong>().SubView(CacheSize * idx, CacheSize), scratchIndices.SubView((NumFineBuckets / 2) * idx, NumFineBuckets / 2), solutionCount.SubView(block, 1));

            //if (Group.IsFirstThread)
            //{
            //    while (true)
            //    {
            //        if (Atomic.Exchange(ref heapLocker[0], 1) == 0)
            //        {
            //            MemoryFence.SystemLevel();
            //            var old = Atomic.Add(ref heapCounter[0], -1);

            //            var delegateLocation = old - 1;
            //            if (heapDelegation[delegateLocation] != 0xFFFF)
            //            {
            //                Interop.WriteLine("Heap not in use. Location: {0}. New: {1}", delegateLocation, heapLocation);
            //            }
            //            heapDelegation[delegateLocation] = (byte)heapLocation;

            //            //Interop.WriteLine("End block {0}. Heap Location: {1}. Delegate Location: {2}", block, heapLocation, delegateLocation);
            //            Atomic.Exchange(ref heapLocker[0], 0);

            //            break;
            //        }
            //    }

            //}
        }

        #region Solve stage

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SolveStage0(ArrayView<ulong> values, ArrayView<byte> heap, ArrayView<int> stage1IndicesCounts, int idx, ArrayView<ulong> cache)
        {
            ClearIndex(ref stage1IndicesCounts[0], idx);

            ArrayView<ushort> stage1IndicesData = Stage1IndiceData(heap);
            ArrayView<ulong> stage1Data = Stage1Data(heap);

            Group.Barrier();

            //512 loops per thread
            //32 ulong values that can be cached

            for (int i = idx; i < IndexSpace; i += BlockSize)
            {
                //LoadToSharedU8(ref cache[0], ref stage1Data[stage1Index + itemIdx]);

                ulong value = values[i];

                int bucketIdx = (int)(value & 255);

                int itemIdx = Atomic.Add(ref stage1IndicesCounts[bucketIdx], 1);

                if (itemIdx < CoarseBucketItems)
                {
                    int index = bucketIdx * CoarseBucketItems + itemIdx;

                    //Convert stage data to be sequential data
                    stage1IndicesData[index] = (ushort)(i); //16 bytes
                    stage1Data[index] = (value >> 8) & 0x000FFFFFFFFFFFFF; //52 bits, reduce
                }
            }
        }

        private static void SolveStage1(ArrayView<byte> heap, ArrayView<ushort> scratchData, ArrayView<int> stage1IndicesCounts, ArrayView<int> stage2IndicesCounts, int bucketIdx, ArrayView<ulong> cache, ArrayView<byte> scratchIndicesCounts)
        {
            ClearIndex(ref stage2IndicesCounts[0], bucketIdx);

            ArrayView<ulong> stage1Data = Stage1Data(heap);
            ArrayView<ulong> stage2Data = Stage2Data(heap);

            Group.Barrier();

            bucketIdx++;

            //for (bucketIdx = Group.IdxX; bucketIdx < (256 / 2 + 1); bucketIdx += BlockSize)
            {
                int cplBucket = InvertBucket(bucketIdx);

                ClearScratchIndices(ref scratchIndicesCounts.Cast<ulong>()[0]);

                int cplBuckSize = Math.Min(stage1IndicesCounts[cplBucket], CoarseBucketItems);

                //Load 4 values into shared memory, 336 total, ulong
                var stage1Index = cplBucket * CoarseBucketItems;

                ushort itemIdx = 0;

                while (itemIdx < cplBuckSize)
                {
                    LoadToSharedU8(ref cache[0], ref stage1Data[stage1Index + itemIdx]);

                    for (int i = 0; i < CacheSize && itemIdx < cplBuckSize; i++, itemIdx++)
                    {
                        ulong value = cache[i];
                        var fineBuckIdx = (int)(value & 127);
                        var fineItemIdxData = scratchIndicesCounts[fineBuckIdx / 2];

                        var shift = (4 * (fineBuckIdx & 1));
                        var mask = 0x0F << shift;

                        var fineItemIdx = (fineItemIdxData & mask) >> shift; //1, 0 | 3, 2

                        if (fineItemIdx < FineBucketItems)
                        {
                            var otherValue = ~mask & fineItemIdxData;
                            var newFineItemIdx = (fineItemIdx + 1) << shift;

                            scratchIndicesCounts[fineBuckIdx / 2] = (byte)(newFineItemIdx | otherValue);
                            scratchData[fineBuckIdx * FineBucketItems + fineItemIdx] = itemIdx;

                            //if (bucketIdx == cplBucket)
                            //{
                            //    var fineCplBucket = (int)((-fineBuckIdx) & 127);
                            //    var fineCplSize = (scratchIndicesCounts[fineCplBucket / 2] >> (4 * (fineCplBucket & 1))) & 0x0F;

                            //    //Use index of the current value to store 4 ushort values from scratchData
                            //    var scratchDataIndex = fineCplBucket * 12;
                            //    var stage1DataIndex = cplBucket * CoarseBucketItems;

                            //    int fineIdx = 0;

                            //    while (fineIdx < fineCplSize)
                            //    {
                            //        LoadToSharedU2(ref cache.Cast<ushort>()[0], ref scratchData[scratchDataIndex + fineIdx]);

                            //        for (int z = 0; z < 4 && fineIdx < fineCplSize; ++z, ++fineIdx)
                            //        {
                            //            var cplIndex = cache.Cast<ushort>()[z];

                            //            ulong cplValue = stage1Data[stage1DataIndex + cplIndex];
                            //            ulong sum = value + cplValue;

                            //            sum >>= 7;

                            //            var s2BuckId = (int)(sum & 255);
                            //            var s2ItemId = Atomic.Add(ref stage2IndicesCounts[s2BuckId], 1);

                            //            if (s2ItemId < CoarseBucketItems)
                            //            {
                            //                var bIndex = s2BuckId * CoarseBucketItems;

                            //                var indice = (ulong)((itemIdx) << 17 | (cplIndex) << 8 | (bucketIdx));
                            //                var res = (sum >> 8) | (indice << 38);

                            //                stage2Data[bIndex + s2ItemId] = res; //37 bits
                            //            }
                            //        }
                            //    }
                            //}
                        }
                    }
                }

                //if (bucketIdx != cplBucket)
                {
                    int buckSize = Math.Min(stage1IndicesCounts[(int)bucketIdx], CoarseBucketItems);

                    stage1Index = bucketIdx * CoarseBucketItems;

                    itemIdx = 0;

                    while (itemIdx < buckSize)
                    {
                        LoadToSharedU8(ref cache[0], ref stage1Data[stage1Index + itemIdx]);

                        for (int i = 0; i < CacheSize && itemIdx < buckSize; i++, itemIdx++)
                        {
                            ulong value = cache[i] + 1;

                            var fineBuckIdx = (int)(value & 127);
                            var fineCplBucket = (int)((-fineBuckIdx) & 127);
                            var fineCplSize = (scratchIndicesCounts[fineCplBucket / 2] >> (4 * (fineCplBucket & 1))) & 0x0F;

                            //Use index of the current value to store 4 ushort values from scratchData
                            var scratchDataIndex = fineCplBucket * 12;
                            var stage1DataIndex = cplBucket * CoarseBucketItems;

                            int fineIdx = 0;

                            while (fineIdx < fineCplSize)
                            //for (fineIdx = 0; fineIdx < fineCplSize && fineIdx < FineBucketItems; fineIdx++)
                            {
                                ulong ulCpl = scratchData.SubView(scratchDataIndex + fineIdx).Cast<ulong>()[0];

                                for (int z = 0; fineIdx < fineCplSize && z < 4; ++z, ++fineIdx)
                                {
                                    var cplIndex = (ushort)(ulCpl >> (16 * z)) & 0xFFFF;

                                    ulong cplValue = stage1Data[stage1DataIndex + cplIndex];
                                    ulong sum = value + cplValue;

                                    sum >>= 7;

                                    var s2BuckId = (int)(sum & 255);
                                    var s2ItemId = Atomic.Add(ref stage2IndicesCounts[s2BuckId], 1);

                                    if (s2ItemId < CoarseBucketItems)
                                    {
                                        var bIndex = s2BuckId * CoarseBucketItems;

                                        var indice = (ulong)((itemIdx) << 17 | (cplIndex) << 8 | (bucketIdx));
                                        var res = (sum >> 8) | (indice << 38);

                                        stage2Data[bIndex + s2ItemId] = res; //37 bits
                                    }
                                }
                            }
                        }
                    }
                }
            }

            Group.Barrier();
        }

        private static void SolveStage2(ArrayView<byte> heap, ArrayView<ushort> scratchData, ArrayView<int> stage2IndicesCounts, ArrayView<int> stage3IndicesCounts, int bucketIdx, ArrayView<ulong> cache, ArrayView<byte> scratchIndicesCounts)
        {
            ClearIndex(ref stage3IndicesCounts[0], bucketIdx);

            //ArrayView<uint> stage3IndicesData = heap.SubView(1033728 + 172032, NumCoarseBuckets * CoarseBucketItems * sizeof(uint)).Cast<uint>();
            ArrayView<ulong> stage2Data = Stage2Data(heap);
            ArrayView<ulong> stage3Data = Stage3Data(heap);

            Group.Barrier();

            bucketIdx++;

            //for (bucketIdx = Group.IdxX; bucketIdx < (256 / 2 + 1); bucketIdx += BlockSize)
            {
                int cplBucket = InvertBucket(bucketIdx);

                ClearScratchIndices(ref scratchIndicesCounts.Cast<ulong>()[0]);

                //if (Group.IsFirstThread)
                //{
                //    for (int i = 0; i < 64; i++)
                //    {
                //        Interop.Write("{0}, ", scratchIndicesCounts[i]);
                //    }
                //}

                int cplBuckSize = Math.Min(stage2IndicesCounts[cplBucket], CoarseBucketItems);

                //Load 4 values into shared memory, 336 total, ulong
                var stage1Index = cplBucket * CoarseBucketItems;

                ushort itemIdx = 0;

                while (itemIdx < cplBuckSize)
                {
                    LoadToSharedU8(ref cache[0], ref stage2Data[stage1Index + itemIdx]);

                    for (int i = 0; i < CacheSize && itemIdx < cplBuckSize; i++, itemIdx++)
                    {
                        ulong value = cache[i] & 0x0000001FFFFFFFFF;
                        var fineBuckIdx = (int)(value & 127);
                        var fineItemIdxData = scratchIndicesCounts[fineBuckIdx / 2];

                        var shift = (4 * (fineBuckIdx & 1));
                        var mask = 0x0F << shift;

                        var fineItemIdx = (fineItemIdxData & mask) >> shift; //1, 0 | 3, 2

                        if (fineItemIdx < FineBucketItems)
                        {
                            var otherValue = ~mask & fineItemIdxData;
                            var newFineItemIdx = (fineItemIdx + 1) << shift;

                            //if (Group.IsFirstThread)
                            //{
                            //    Interop.WriteLine("Before: {0}, After: {1}. {2}, {3}", fineItemIdxData, newFineItemIdx | otherValue, otherValue, newFineItemIdx);
                            //}

                            scratchIndicesCounts[fineBuckIdx / 2] = (byte)(newFineItemIdx | otherValue);
                            scratchData[fineBuckIdx * FineBucketItems + fineItemIdx] = itemIdx;

                            //if (bucketIdx == cplBucket)
                            //{
                            //    var fineCplBucket = (int)((-fineBuckIdx) & 127);
                            //    var fineCplSize = (scratchIndicesCounts[fineCplBucket / 2] >> (4 * (fineCplBucket & 1))) & 0x0F;

                            //    //Use index of the current value to store 4 ushort values from scratchData
                            //    var scratchDataIndex = fineCplBucket * 12;
                            //    var stage1DataIndex = cplBucket * CoarseBucketItems;

                            //    int fineIdx = 0;

                            //    while (fineIdx < fineCplSize)
                            //    {
                            //        LoadToSharedU2(ref cache.Cast<ushort>()[0], ref scratchData[scratchDataIndex + fineIdx]);

                            //        for (int z = 0; z < 4 && fineIdx < fineCplSize; ++z, ++fineIdx)
                            //        {
                            //            var cplIndex = cache.Cast<ushort>()[z];

                            //            ulong cplValue = stage2Data[stage1DataIndex + cplIndex] & 0x0000001FFFFFFFFF;
                            //            ulong sum = value + cplValue;

                            //            sum >>= 7;

                            //            var s2BuckId = (int)(sum & 255);
                            //            var s2ItemId = Atomic.Add(ref stage3IndicesCounts[s2BuckId], 1);

                            //            if (s2ItemId < CoarseBucketItems)
                            //            {
                            //                var bIndex = s2BuckId * CoarseBucketItems;

                            //                var indice = (ulong)((itemIdx) << 17 | (cplIndex) << 8 | (bucketIdx));
                            //                var res = (sum >> 8) | (indice << 38);

                            //                stage3Data[bIndex + s2ItemId] = res; //37 bits
                            //            }
                            //        }
                            //    }
                            //}
                        }
                    }
                }

                //if (bucketIdx != cplBucket)
                {
                    int buckSize = Math.Min(stage2IndicesCounts[(int)bucketIdx], CoarseBucketItems);

                    stage1Index = bucketIdx * CoarseBucketItems;

                    itemIdx = 0;

                    while (itemIdx < buckSize)
                    {
                        LoadToSharedU8(ref cache[0], ref stage2Data[stage1Index + itemIdx]);

                        for (int i = 0; i < CacheSize && itemIdx < buckSize; i++, itemIdx++)
                        {
                            ulong value = (cache[i] & 0x0000001FFFFFFFFF) + 1;

                            var fineBuckIdx = (int)(value & 127);
                            var fineCplBucket = (int)((-fineBuckIdx) & 127);
                            var fineCplSize = (scratchIndicesCounts[fineCplBucket / 2] >> (4 * (fineCplBucket & 1))) & 0x0F;

                            //Use index of the current value to store 4 ushort values from scratchData
                            var scratchDataIndex = fineCplBucket * 12;
                            var stage1DataIndex = cplBucket * CoarseBucketItems;

                            int fineIdx = 0;

                            while (fineIdx < fineCplSize)
                            {
                                ulong ulCpl = scratchData.SubView(scratchDataIndex + fineIdx).Cast<ulong>()[0];

                                for (int z = 0; fineIdx < fineCplSize && z < 4; ++z, ++fineIdx)
                                {
                                    var cplIndex = (ushort)(ulCpl >> (16 * z)) & 0xFFFF;

                                    ulong cplValue = stage2Data[stage1DataIndex + cplIndex] & 0x0000001FFFFFFFFF;
                                    ulong sum = value + cplValue;

                                    sum >>= 7;

                                    var s2BuckId = (int)(sum & 255);
                                    var s2ItemId = Atomic.Add(ref stage3IndicesCounts[s2BuckId], 1);

                                    if (s2ItemId < CoarseBucketItems)
                                    {
                                        var bIndex = s2BuckId * CoarseBucketItems;

                                        var indice = (ulong)((itemIdx) << 17 | (cplIndex) << 8 | (bucketIdx));
                                        var res = (sum >> 8) | (indice << 38);

                                        stage3Data[bIndex + s2ItemId] = res; //37 bits
                                    }
                                }
                            }
                        }
                    }
                }
            }

            Group.Barrier();
        }

        private static void SolveStage3(ArrayView<byte> heap, ArrayView<ushort> scratchData, ArrayView<ushort> solutions, ArrayView<int> stage3IndicesCounts, int bucketIdx,
            ArrayView<ulong> cache, ArrayView<byte> scratchIndicesCounts, ArrayView<uint> solutionCount)
        {
            ArrayView<ulong> stage3Data = Stage3Data(heap);

            bucketIdx++;
            //for (bucketIdx = Group.IdxX; bucketIdx < (256 / 2 + 1); bucketIdx += BlockSize)
            {
                int cplBucket = InvertBucket(bucketIdx);

                ClearScratchIndices(ref scratchIndicesCounts.Cast<ulong>()[0]);

                int cplBuckSize = Math.Min(stage3IndicesCounts[cplBucket], CoarseBucketItems);

                //Load 4 values into shared memory, 336 total, ulong
                var stage1Index = cplBucket * CoarseBucketItems;

                ushort itemIdx = 0;

                while (itemIdx < cplBuckSize)
                {
                    LoadToSharedU8(ref cache[0], ref stage3Data[stage1Index + itemIdx]);

                    for (int i = 0; i < CacheSize && itemIdx < cplBuckSize; i++, itemIdx++)
                    {
                        ulong value = cache[i] & 0x0000001FFFFFFFFF;
                        var fineBuckIdx = (int)(value & 127);
                        var fineItemIdxData = scratchIndicesCounts[fineBuckIdx / 2];

                        var shift = (4 * (fineBuckIdx & 1));
                        var mask = 0x0F << shift;

                        var fineItemIdx = (fineItemIdxData & mask) >> shift; //1, 0 | 3, 2

                        if (fineItemIdx < FineBucketItems)
                        {
                            var otherValue = ~mask & fineItemIdxData;
                            var newFineItemIdx = (fineItemIdx + 1) << shift;

                            //if (Group.IsFirstThread)
                            //{
                            //    Interop.WriteLine("Before: {0}, After: {1}. {2}, {3}", fineItemIdxData, newFineItemIdx | otherValue, otherValue, newFineItemIdx);
                            //}

                            scratchIndicesCounts[fineBuckIdx / 2] = (byte)(newFineItemIdx | otherValue);
                            scratchData[fineBuckIdx * FineBucketItems + fineItemIdx] = itemIdx;

                            //if (bucketIdx == cplBucket)
                            //{
                            //    var fineCplBucket = (int)((-fineBuckIdx) & 127);
                            //    var fineCplSize = (scratchIndicesCounts[fineCplBucket / 2] >> (4 * (fineCplBucket & 1))) & 0x0F;

                            //    //Use index of the current value to store 4 ushort values from scratchData
                            //    var scratchDataIndex = fineCplBucket * 12;
                            //    var stage3DataIndex = cplBucket * CoarseBucketItems;

                            //    int fineIdx = 0;

                            //    while (fineIdx < fineCplSize)
                            //    {
                            //        //Causes issues for some reason
                            //        //LoadToSharedU2(ref cache.Cast<ushort>()[0], ref scratchData[scratchDataIndex + fineIdx]);

                            //        for (int z = 0; z < 4 && fineIdx < fineCplSize; ++z, ++fineIdx)
                            //        {
                            //            var cplIndex = scratchData[scratchDataIndex + fineIdx];// cache.Cast<ushort>()[z];

                            //            ulong cplValue = stage3Data[stage3DataIndex + cplIndex] & 0x0000001FFFFFFFFF;
                            //            ulong sum = value + cplValue;

                            //            sum >>= 7;

                            //            if (((sum & (1ul << 15) - 1)) == 0)
                            //            {
                            //                uint itemLeft = (uint)(stage3Data[bucketIdx * CoarseBucketItems + itemIdx] >> 38);
                            //                uint itemRight = (uint)(stage3Data[stage3DataIndex + cplIndex] >> 38);

                            //                var loc = Atomic.Add(ref solutionCount[0], 1);

                            //                BuildSolution(heap, solutions.SubView(loc * 8, 8), itemLeft, itemRight);

                            //                if (loc >= 8)
                            //                {
                            //                    return;
                            //                }
                            //            }
                            //        }
                            //    }
                            //}
                        }
                    }
                }

                //if (bucketIdx != cplBucket)
                {
                    int buckSize = Math.Min(stage3IndicesCounts[(int)bucketIdx], CoarseBucketItems);

                    stage1Index = bucketIdx * CoarseBucketItems;

                    itemIdx = 0;

                    while (itemIdx < buckSize)
                    {
                        LoadToSharedU8(ref cache[0], ref stage3Data[stage1Index + itemIdx]);

                        for (int i = 0; i < CacheSize && itemIdx < buckSize; i++, itemIdx++)
                        {
                            ulong value = (cache[i] & 0x0000001FFFFFFFFF) + 1;

                            var fineBuckIdx = (int)(value & 127);
                            var fineCplBucket = (int)((-fineBuckIdx) & 127);
                            var fineCplSize = (scratchIndicesCounts[fineCplBucket / 2] >> (4 * (fineCplBucket & 1))) & 0x0F;

                            //Use index of the current value to store 4 ushort values from scratchData
                            var scratchDataIndex = fineCplBucket * 12;
                            var stage3DataIndex = cplBucket * CoarseBucketItems;

                            int fineIdx = 0;

                            while (fineIdx < fineCplSize)
                            {
                                ulong ulCpl = scratchData.SubView(scratchDataIndex + fineIdx).Cast<ulong>()[0];

                                for (int z = 0; fineIdx < fineCplSize && z < 4; ++z, ++fineIdx)
                                {
                                    var cplIndex = (ushort)(ulCpl >> (16 * z)) & 0xFFFF;

                                    ulong cplValue = stage3Data[stage3DataIndex + cplIndex] & 0x0000001FFFFFFFFF;
                                    ulong sum = value + cplValue;

                                    sum >>= 7;

                                    if (((sum & (1ul << 15) - 1)) == 0)
                                    {
                                        uint itemLeft = (uint)(stage3Data[bucketIdx * CoarseBucketItems + itemIdx] >> 38);
                                        uint itemRight = (uint)(stage3Data[stage3DataIndex + cplIndex] >> 38);

                                        var loc = Atomic.Add(ref solutionCount[0], 1);

                                        BuildSolution(heap, solutions.SubView(loc * 8, 8), itemLeft, itemRight);

                                        if (loc >= 8)
                                        {
                                            return;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            Group.Barrier();
        }

        #endregion

        #region Build stage

        private static void BuildSolutionStage1(ArrayView<byte> heap, ArrayView<ushort> solution, uint root)
        {
            int bucket = (int)(root & 255);
            int bucketInv = -bucket & 255;
            int leftParentIdx = (int)(root >> 17);
            int rightParentIdx = (int)(root >> 8) & 511;

            ArrayView<ushort> stage1IndicesData = Stage1IndiceData(heap);

            ushort leftParent = stage1IndicesData[bucket * CoarseBucketItems + leftParentIdx];
            ushort rightParent = stage1IndicesData[bucketInv * CoarseBucketItems + rightParentIdx];

            solution[0] = leftParent;
            solution[1] = rightParent;
        }

        private static void BuildSolutionStage2(ArrayView<byte> heap, ArrayView<ushort> solution, uint root)
        {
            int bucket = (int)(root & 255);
            int bucketInv = -bucket & 255;
            int leftParentIdx = (int)(root >> 17);
            int rightParentIdx = (int)(root >> 8) & 511;

            ArrayView<ulong> stage2Data = Stage2Data(heap);

            uint leftParent = (uint)(stage2Data[bucket * CoarseBucketItems + leftParentIdx] >> 38);
            uint rightParent = (uint)(stage2Data[bucketInv * CoarseBucketItems + rightParentIdx] >> 38);

            BuildSolutionStage1(heap, solution, leftParent);
            BuildSolutionStage1(heap, solution.SubView(2), rightParent);
        }

        private static void BuildSolution(ArrayView<byte> heap, ArrayView<ushort> solution, uint itemLeft, uint itemRight)
        {
            BuildSolutionStage2(heap, solution, itemLeft);
            BuildSolutionStage2(heap, solution.SubView(4, 4), itemRight);
        }

        #endregion

    }
}