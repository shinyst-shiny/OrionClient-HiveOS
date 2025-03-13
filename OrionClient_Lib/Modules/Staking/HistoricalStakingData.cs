using Solnet.Wallet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace OrionClientLib.Modules.Staking
{
    public class HistoricalStakingData
    {
        public Dictionary<string, BoostRewardData> BoostData { get; private set; } = new Dictionary<string, BoostRewardData>();

        public int TotalTransactionsToPull => BoostData.Sum(x => x.Value.TransactionCache.Where(y => !y.DataPulled).Count());
        public int TotalTransactions => BoostData.Sum(x => x.Value.TransactionCache.Count());
        public int GetArchivalTransactions(DateTimeOffset archivalTime) => BoostData.Sum(x => x.Value.TransactionCache.Where(y => !y.DataPulled && y.Timestamp < archivalTime.ToUnixTimeSeconds()).Count());

        public void Calculate()
        {
            foreach(var b in BoostData)
            {
                b.Value.Calculate();
            }
        }

        public class BoostRewardData
        {
            public string Boost { get; set; }

            public string MostRecentHash => TransactionCache.FirstOrDefault()?.Signature;

            [JsonIgnore]
            public List<HistoricalDay> Days { get; private set; } = new List<HistoricalDay>();
            public List<StakeCheckpointTransaction> TransactionCache { get; private set; } = new List<StakeCheckpointTransaction>();

            public BoostRewardData(string boostProof)
            {
                Boost = boostProof;
            }

            public void Calculate()
            {
                if(TransactionCache.Count == 0)
                {
                    return;
                }

                StakeCheckpointTransaction startTransaction = null;
                StakeCheckpointTransaction prevTransaction = null;

                List<HistoricalCheckpoint> historicalCheckpoints = new List<HistoricalCheckpoint>();

                for (int i = TransactionCache.Count - 1; i >= 0; i--)
                {
                    StakeCheckpointTransaction transaction = TransactionCache[i];

                    if(transaction.CheckpointStarted)
                    {
                        //First transaction
                        if(startTransaction == null)
                        {
                            startTransaction = transaction;
                        }
                        else
                        {
                            //Previous transaction is the end
                            historicalCheckpoints.Add(new HistoricalCheckpoint
                            {
                                RewardAmount = startTransaction.RewardAmount,
                                CheckpointStart = startTransaction.Timestamp,
                                CheckpointEnd = prevTransaction.Timestamp
                            });

                            startTransaction = transaction;
                        }
                    }

                    prevTransaction = transaction;
                }

                if(startTransaction == null)
                {
                    return;
                }

                //Final checkpoint
                historicalCheckpoints.Add(new HistoricalCheckpoint
                {
                    RewardAmount = startTransaction.RewardAmount,
                    CheckpointStart = startTransaction.Timestamp,
                    CheckpointEnd = prevTransaction.Timestamp
                });

                for(int i = 0; i < historicalCheckpoints.Count - 1; i++)
                {
                    var current = historicalCheckpoints[i];
                    var next = historicalCheckpoints[i + 1];

                    next.TotalTime = next.CheckpointStart - current.CheckpointEnd;
                }

                Days.Clear();

                //Group by days
                foreach (var group in historicalCheckpoints.GroupBy(x => DateTimeOffset.FromUnixTimeSeconds(x.CheckpointStart).Date).OrderByDescending(x => x.Key))
                {
                    var historicalDay = new HistoricalDay
                    {
                        BoostName = Boost,
                        DayStart = new DateTimeOffset(group.Key).ToUnixTimeSeconds(),
                    };

                    historicalDay.Checkpoints.AddRange(group.OrderByDescending(x => x.CheckpointStart));

                    Days.Add(historicalDay);
                }
            }
        }

        public class HistoricalDay
        {
            public string BoostName { get; set; }
            public long DayStart { get; set; }
            public ulong TotalRewards => Checkpoints.Aggregate(0UL, (a, b) => a + b.RewardAmount);
            public long TotalLockTime => Checkpoints.Sum(x => x.LockTime);
            public long TotalTime => Checkpoints.Sum(x => x.TotalTime);

            public List<HistoricalCheckpoint> Checkpoints { get; private set; } = new List<HistoricalCheckpoint>();
        }

        public class HistoricalCheckpoint
        {
            public ulong RewardAmount { get; set; }
            public long CheckpointStart { get; set; }
            public long CheckpointEnd { get; set; }
            public long TotalTime { get; set; }
            public long LockTime => CheckpointEnd - CheckpointStart;
        }
    }
}
