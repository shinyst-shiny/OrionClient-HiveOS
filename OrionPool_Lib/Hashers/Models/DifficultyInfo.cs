using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Hashers.Models
{
    public class DifficultyInfo
    {
        public ulong BestNonce { get; private set; }
        public int BestDifficulty { get; private set; }
        public byte[] BestSolution { get; private set; }
        public int ChallengeId { get; private set; }

        private object _locker = new object();

        public DifficultyInfo()
        {

        }

        public DifficultyInfo(int challengeId)
        {
            ChallengeId = challengeId;
        }

        public void UpdateDifficulty(int difficulty, byte[] solution, ulong nonce)
        {
            lock (_locker)
            {
                if (difficulty <= BestDifficulty)
                {
                    return;
                }

                BestDifficulty = difficulty;
                BestNonce = nonce;
                BestSolution = solution;
            }
        }

        public DifficultyInfo GetUpdateCopy()
        {
            lock (_locker)
            {
                return new DifficultyInfo
                {
                    BestDifficulty = BestDifficulty,
                    BestNonce = BestNonce,
                    BestSolution = BestSolution,
                    ChallengeId = ChallengeId
                };
            }
        }
    }
}
