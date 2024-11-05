using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Hashers.Models
{
    public class HashrateInfo
    {
        public bool IsCPU { get; set; }
        public int Index { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public TimeSpan TotalTime { get; set; }
        public ulong NumSolutions { get; set; }
        public ulong NumNonces { get; set; }
        public int HighestDifficulty { get; set; }
        public int CurrentThreads { get; set; }
        public int ChallengeId { get; set; }
        public ulong ChallengeSolutions { get; set; }

        public HashesPerSecond NoncePerSecond => new HashesPerSecond(NumNonces, ExecutionTime);
        public HashesPerSecond SolutionsPerSecond => new HashesPerSecond(NumSolutions, ExecutionTime);
        public HashesPerSecond ChallengeSolutionsPerSecond => new HashesPerSecond(ChallengeSolutions, TotalTime);
    }


    public class HashesPerSecond
    {
        public ulong Count { get; private set; }
        public TimeSpan Time { get; private set; }

        public double Speed => Count / Time.TotalSeconds;

        public HashesPerSecond(ulong count, TimeSpan time)
        {
            Count = count;
            Time = time;
        }

        public override string ToString()
        {
            return ConvertToPrettyFormat(Speed);
        }

        public static bool operator >(HashesPerSecond a, HashesPerSecond b) => a.Speed > b.Speed;
        public static bool operator <(HashesPerSecond a, HashesPerSecond b) => a.Speed < b.Speed;
        public static bool operator >=(HashesPerSecond a, HashesPerSecond b) => a.Speed >= b.Speed;
        public static bool operator <=(HashesPerSecond a, HashesPerSecond b) => a.Speed <= b.Speed;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        string ConvertToPrettyFormat(double hashes)
        {
            if (hashes > 1000000000)
            {
                return $"{hashes / 1000000000:0.000} GH/s";
            }
            else if (hashes > 1000000)
            {
                return $"{hashes / 1000000:0.000} MH/s";
            }
            else if (hashes > 1000)
            {
                return $"{hashes / 1000:0.000} KH/s";
            }

            return $"{hashes:0.000} H/s";
        }
    }
}
