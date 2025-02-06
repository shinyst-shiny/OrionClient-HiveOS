using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionEventLib.Events.Mining
{
    public class SubmissionResultEvent : MiningEvent
    {
        public override SubEventTypes SubEventType => SubEventTypes.SubmissionResult;

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

        public override void Deserialize(EventDeserializer eventDeserializer)
        {
            base.Deserialize(eventDeserializer);

            SubmissionResult = eventDeserializer.ReadBytes();
        }

        public override string ToString()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine($"Event: {EventType}. Subtype: {SubEventType}. Time: {DateTimeOffset.FromUnixTimeSeconds(Timestamp).ToLocalTime().DateTime.ToLongTimeString()}");
            builder.AppendLine($"Data length: {SubmissionResult?.Length ?? 0}");

            return builder.ToString();
        }
    }
}
