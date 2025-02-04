using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionEventLib.Events.Mining
{
    public class MiningStartEvent : MiningEvent
    {
        public override SubEventTypes SubEventType => SubEventTypes.Start;
        public required bool CPUEnabled { get; set; }
        public required int CPUThreads { get; set; }

        public List<DeviceInformation> Devices { get; private set; } = new List<DeviceInformation>();
        public required int GPUBatchSize { get; set; }
        public required int GPUBlockSize { get; set; }
        public required int ProgramGenerationThreads { get; set; }

        public required string Pool { get; set; }
        public required string CPUHasher { get; set; }
        public required string GPUHasher { get; set; }

        public override ArraySegment<byte> Serialize(EventSerializer eventSerializer)
        {
            base.Serialize(eventSerializer);

            eventSerializer.WriteBool(CPUEnabled);
            eventSerializer.WriteS32(CPUThreads);

            eventSerializer.WriteU16((ushort)Devices.Count);

            for(int i =0; i < Devices.Count; i++)
            {
                eventSerializer.WriteString(Devices[i].Name);
                eventSerializer.WriteS32(Devices[i].Id);
            }

            eventSerializer.WriteS32(GPUBatchSize);
            eventSerializer.WriteS32(GPUBlockSize);
            eventSerializer.WriteS32(ProgramGenerationThreads);

            eventSerializer.WriteString(Pool);
            eventSerializer.WriteString(CPUHasher);
            eventSerializer.WriteString(GPUHasher);

            return eventSerializer.GetData();
        }


        public class DeviceInformation
        {
            public string Name { get; set; }
            public int Id { get; set; }
        }
    }
}
