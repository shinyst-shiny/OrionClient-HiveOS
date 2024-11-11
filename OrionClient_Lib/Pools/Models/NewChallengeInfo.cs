using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools.Models
{
    public class NewChallengeInfo
    {
        public ulong StartNonce { get; set; }
        public ulong EndNonce { get; set; }
        public byte[] Challenge { get; set; }
        public int ChallengeId { get; set; }
    }
}
