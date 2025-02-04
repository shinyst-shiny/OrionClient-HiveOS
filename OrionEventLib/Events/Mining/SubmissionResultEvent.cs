using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionEventLib.Events.Mining
{
    public class SubmissionResultEvent : MiningEvent
    {
        public override SubEventTypes SubEventType => SubEventTypes.NewChallenge;

        /// <summary>
        /// Value depends on the pool
        /// </summary>
        public required object SubmissionResult { get; set; }
    }
}
