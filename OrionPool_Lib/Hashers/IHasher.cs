using OrionClientLib.Hashers.Models;
using OrionClientLib.Pools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Hashers
{
    public interface IHasher
    {
        public enum Hardware { CPU, GPU };

        public Hardware HardwareType { get; }
        public string Name { get; }
        public string Description { get; }
        public bool Initialized { get; }
        public TimeSpan CurrentChallengeTime { get; }

        public event EventHandler<HashrateInfo> OnHashrateUpdate;

        public bool IsSupported();
        public bool NewChallenge(int challengeId, Span<byte> challenge, ulong startNonce, ulong endNonce);
        public bool Initialize(IPool pool, int threads);
        public Task StopAsync();
        public void SetThreads(int totalThreads);
    }
}
