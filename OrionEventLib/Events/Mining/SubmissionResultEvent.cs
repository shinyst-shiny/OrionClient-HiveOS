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
        /// Byte data of submission response
        /// </summary>
        public required byte[] SubmissionResult { get; set; }

        public override ArraySegment<byte> Serialize(EventSerializer eventSerializer)
        {
            base.Serialize(eventSerializer);

            eventSerializer.WriteBytes(SubmissionResult);

            return eventSerializer.GetData();
        }
    }
}
