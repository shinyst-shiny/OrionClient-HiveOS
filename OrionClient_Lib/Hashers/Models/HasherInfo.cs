using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Hashers.Models
{
    public class HasherInfo
    {
        public ulong BatchSize { get; set; } = 256;
        public ulong CurrentNonce { get; set; } = 0;

        public ulong StartNonce { get; private set; } = 0;
        public ulong EndNonce { get; private set; } = 0;
        public byte[] Challenge { get; private set; }
        public int ChallengeId { get; private set; }

        public ulong TotalSolutions => _totalSolutions;

        private ulong _totalSolutions = 0;

        public DifficultyInfo DifficultyInfo { get; private set; } = new DifficultyInfo();

        public void NewChallenge(ulong start, ulong end, byte[] challenge, int challengeId)
        {
            StartNonce = start;
            EndNonce = end;
            CurrentNonce = start;
            _totalSolutions = 0;
            Challenge = challenge;
            ChallengeId = challengeId;
            DifficultyInfo = new DifficultyInfo(challengeId);
        }

        public void UpdateDifficulty(int difficulty, byte[] solution, ulong nonce, bool isCPU = true)
        {
            if (nonce >= StartNonce && nonce <= EndNonce)
            {
                DifficultyInfo.UpdateDifficulty(difficulty, solution, nonce, isCPU);
            }
        }

        public void AddSolutionCount(ulong solutions)
        {
            Interlocked.Add(ref _totalSolutions, solutions);
        }
    }
}
