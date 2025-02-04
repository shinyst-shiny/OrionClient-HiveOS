using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionEventLib.Events.Mining
{
    public abstract class MiningEvent : OrionEvent
    {
        public override EventTypes EventType => EventTypes.Mining;
    }
}
