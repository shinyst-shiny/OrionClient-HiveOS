using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionEventLib.Events.Mining
{
    ////Difficulty is given on a hashupdate
    //public class DifficultySubmission : MiningEvent
    //{
    //    public override SubEventTypes SubEventType => SubEventTypes.DifficultySubmission;
    //    public required int Difficulty { get; set; }
    //    public required ulong Nonce { get; set; }

    //    public bool IsCPU => DeviceId == -1;
    //    public required int DeviceId { get; set; } = -1;

    //    public override ArraySegment<byte> Serialize(EventSerializer eventSerializer)
    //    {
    //        base.Serialize(eventSerializer);

    //        eventSerializer.WriteS32(Difficulty);
    //        eventSerializer.WriteU64(Nonce);
    //        eventSerializer.WriteBool(IsCPU);
    //        eventSerializer.WriteS32(DeviceId);

    //        return eventSerializer.GetData();
    //    }
    //}
}
