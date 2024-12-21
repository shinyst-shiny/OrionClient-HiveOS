using OrionClientLib.Hashers.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules.Vanity
{
    public class VanityHashingInfo
    {
        public List<FoundVanity> Vanities { get; set; }
        public int Index { get; set; }
        public TimeSpan PrivateKeyGenerationTime { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public TimeSpan VanitySearchTime { get; set; }
        public TimeSpan Runtime { get; set; }

        public int CurrentBatchSize { get; set; }

        public HashesPerSecond Speed => new HashesPerSecond((ulong)CurrentBatchSize, ExecutionTime);
    }
}
