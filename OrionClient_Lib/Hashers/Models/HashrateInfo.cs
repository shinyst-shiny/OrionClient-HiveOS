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
        public int Index { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public TimeSpan GPUHashXTime { get; set; }
        public TimeSpan GPUEquihashTime { get; set; }
        public TimeSpan TotalTime { get; set; }
        public ulong NumSolutions { get; set; }
        public ulong NumNonces { get; set; }
        public int HighestDifficulty { get; set; }
        public int CurrentThreads { get; set; }
        public int ChallengeId { get; set; }
        public ulong ChallengeSolutions { get; set; }

        //public HashesPerSecond NoncePerSecond => new HashesPerSecond(NumNonces, ExecutionTime);
        public HashesPerSecond SolutionsPerSecond => new HashesPerSecond(NumSolutions, ExecutionTime);
        public HashesPerSecond ChallengeSolutionsPerSecond => new HashesPerSecond(ChallengeSolutions, TotalTime);

        public HashesPerSecond HashxNoncesPerSecond => new HashesPerSecond(NumNonces, GPUHashXTime, true);
        public HashesPerSecond EquihashNoncesPerSecond => new HashesPerSecond(NumNonces, GPUEquihashTime, true);
    }


    public class HashesPerSecond
    {
        public ulong Count { get; private set; }
        public TimeSpan Time { get; private set; }

        public double Speed => Count / Time.TotalSeconds;
        public bool IsNonces { get; private set; }

        public HashesPerSecond(ulong count, TimeSpan time, bool isNonces = false)
        {
            Count = count;
            Time = time;
            IsNonces = isNonces;
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
            string h = IsNonces ? "N" : "H";

            if (hashes > 1000000000)
            {
                return $"{hashes / 1000000000:0.000} G{h}/s";
            }
            else if (hashes > 1000000)
            {
                return $"{hashes / 1000000:0.000} M{h}/s";
            }
            else if (hashes > 1000)
            {
                return $"{hashes / 1000:0.000} K{h}/s";
            }

            return $"{hashes:0.000} {h}/s";
        }
    }
}
