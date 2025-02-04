using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionEventLib.Events.Mining
{
    public class MiningPauseEvent : MiningEvent
    {
        public override SubEventTypes SubEventType => SubEventTypes.Pause;
    }
}
