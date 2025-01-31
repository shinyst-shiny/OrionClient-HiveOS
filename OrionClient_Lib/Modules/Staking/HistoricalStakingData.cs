using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules.Staking
{
    public class HistoricalStakingData
    {
        public Dictionary<string, List<HistoricalDay>> BoostData { get; private set; } = new Dictionary<string, List<HistoricalDay>>();

        public class HistoricalDay
        {
            public long DayStart { get; set; }
            public ulong TotalRewards => Checkpoints.Aggregate(0UL, (a, b) => a + b.RewardAmount);
            public long TotalLockTime => Checkpoints.Sum(x => x.LockTime);

            public List<HistoricalCheckpoint> Checkpoints { get; private set; } = new List<HistoricalCheckpoint>();
        }

        public class HistoricalCheckpoint
        {
            public ulong RewardAmount { get; set; }
            public long CheckpointStart { get; set; }
            public long PayoutStart { get; set; }
            public long CheckpointEnd { get; set; }

            public long LockTime => CheckpointEnd - CheckpointStart;
        }
    }
}
