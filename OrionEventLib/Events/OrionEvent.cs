using System;
using System.Collections.Generic;
using System.Linq;
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
        public virtual long Timestamp => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}
