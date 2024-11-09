using OrionClientLib.Pools.Models;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools.HQPool
{
    internal class PoolSubmissionResponse : IMessage
    {
        public uint Difficulty { get; private set; }
        public double TotalBalance { get; private set; }
        public double TotalRewards { get; private set; }
        public double TopStake { get; private set; }
        public double Multiplier { get; private set; }
        public uint ActiveMiners { get; private set; }
        public byte[] Challenge { get; private set; }
        public ulong BestNonce { get; private set; }
        public uint MinerSuppliedDifficulty { get; private set; }
        public double MinerEarnedRewards { get; private set; }
        public double MinerPercentage { get; private set; }

        public void Deserialize(ArraySegment<byte> data)
        {
            Difficulty = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(1, 4));
            TotalBalance = BinaryPrimitives.ReadDoubleLittleEndian(data.Slice(5, 8));
            TotalRewards = BinaryPrimitives.ReadDoubleLittleEndian(data.Slice(13, 8));
            TopStake = BinaryPrimitives.ReadDoubleLittleEndian(data.Slice(21, 8));
            Multiplier = BinaryPrimitives.ReadDoubleLittleEndian(data.Slice(29, 8));
            ActiveMiners = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(37, 4));
            Challenge = data.Slice(41, 32).ToArray();
            BestNonce = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(73, 8));
            MinerSuppliedDifficulty = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(81, 4));
            MinerEarnedRewards = BinaryPrimitives.ReadDoubleLittleEndian(data.Slice(85, 8));
            MinerPercentage = BinaryPrimitives.ReadDoubleLittleEndian(data.Slice(93, 8));
        }

        public ArraySegment<byte> Serialize()
        {
            throw new NotImplementedException();
        }
    }
}
