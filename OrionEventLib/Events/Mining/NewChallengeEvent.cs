using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionEventLib.Events.Mining
{
    public class NewChallengeEvent : MiningEvent
    {
        public override SubEventTypes SubEventType => SubEventTypes.NewChallenge;
        public required byte[] Challenge { get; set; }
        public required ulong StartNonce { get; set; }
        public required ulong EndNonce { get; set; }
        public required int ChallengeId { get; set; }
    }
}
