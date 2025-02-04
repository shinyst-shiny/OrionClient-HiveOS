using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionEventLib.Events
{
    public enum EventTypes { Unknown, Mining, Error };
    public enum SubEventTypes 
    {
        None,
        HashrateUpdate, Start, Pause, NewChallenge, SubmissionResult, DifficultySubmission //Mining Events
    };
}
