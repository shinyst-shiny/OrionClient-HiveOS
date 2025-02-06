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
        public byte HighestDeviceDifficulty { get; set; }

        /// <summary>
        /// Is true when the CPU is taking longer to generate new programs than the GPU took to execute
        /// </summary>
        public required bool CPUStruggling { get; set; }

        public override ArraySegment<byte> Serialize(EventSerializer eventSerializer)
        {
            base.Serialize(eventSerializer);

            eventSerializer.WriteBool(IsCPU);
            eventSerializer.WriteS32(DeviceId);
            eventSerializer.WriteDouble(CurrentHashesPerSecond);
            eventSerializer.WriteDouble(AverageHashesPerSecond);
            eventSerializer.WriteByte(HighestDeviceDifficulty);
            eventSerializer.WriteBool(CPUStruggling);

            return eventSerializer.GetData();
        }

        public override void Deserialize(EventDeserializer eventDeserializer)
        {
            base.Deserialize(eventDeserializer);

            eventDeserializer.Skip(1);
            DeviceId = eventDeserializer.ReadS32();
            CurrentHashesPerSecond = eventDeserializer.ReadDouble();
            AverageHashesPerSecond = eventDeserializer.ReadDouble();
            HighestDeviceDifficulty = eventDeserializer.ReadByte();
            CPUStruggling = eventDeserializer.ReadBool();
        }

        public override string ToString()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine($"Event: {EventType}. Subtype: {SubEventType}. Time: {DateTimeOffset.FromUnixTimeSeconds(Timestamp).ToLocalTime().DateTime.ToLongTimeString()}");
            builder.AppendLine($"Device Id: {DeviceId}. Is CPU: {IsCPU}");
            builder.AppendLine($"Current H/S: {CurrentHashesPerSecond}. Average H/S: {AverageHashesPerSecond}. Best Diff: {HighestDeviceDifficulty}. CPU Struggling: {CPUStruggling}");

            return builder.ToString();
        }
    }
}
