using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace OrionEventLib.Events
{
    public abstract class OrionEvent
    {
        /// <summary>
        /// Main event type
        /// </summary>
        public abstract EventTypes EventType { get; }

        /// <summary>
        /// Subevent type
        /// </summary>
        public abstract SubEventTypes SubEventType { get; }

        /// <summary>
        /// Potential Id passed through arguments
        /// </summary>
        public virtual string? Id { get; set; }

        /// <summary>
        /// Unix timestamp of event time
        /// </summary>
        public virtual long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public virtual ArraySegment<byte> Serialize(EventSerializer eventSerializer)
        {
            eventSerializer.WriteByte((byte)EventType);
            eventSerializer.WriteByte((byte)SubEventType);
            eventSerializer.WriteString(Id);
            eventSerializer.WriteS64(Timestamp);

            return eventSerializer.GetData();
        }

        public virtual void Deserialize(EventDeserializer eventDeserializer)
        {
            eventDeserializer.Seek(4); //Beginning of data
            Id = eventDeserializer.ReadString();
            Timestamp = eventDeserializer.ReadS64();
        }

        public override string ToString()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine($"Event: {EventType}. Subtype: {SubEventType}");

            return builder.ToString();
        }
    }
}
