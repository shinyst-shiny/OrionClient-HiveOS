using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionEventLib.Events.Mining
{
    public class HashrateUpdateEvent : MiningEvent
    {
        public override SubEventTypes SubEventType => SubEventTypes.HashrateUpdate;
        public bool IsCPU => DeviceId == -1;
        public required int DeviceId { get; set; } = -1;
        public required double CurrentHashesPerSecond { get; set; }
        public required double AverageHashesPerSecond { get; set; }

        /// <summary>
        /// Is true when the CPU is taking longer to generate new programs than the GPU took to execute
        /// </summary>
        public required bool CPUStruggling { get; set; }
    }
}
