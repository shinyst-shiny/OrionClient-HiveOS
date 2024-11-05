using OrionClientLib.Hashers.Models;
using OrionClientLib.Pools.Models;
using Solnet.Wallet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools
{
    public interface IPool
    {
        public string PoolName { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public Dictionary<string, string> Features { get; }
        public bool HideOnPoolList { get; }

        public event EventHandler<NewChallengeInfo> OnChallengeUpdate;
        public event EventHandler<string[]> OnMinerUpdate;

        public Task<bool> SetupAsync(CancellationToken token, bool initialSetup = false);

        public Task<bool> ConnectAsync(Wallet wallet, string publicKey);
        public Task<bool> DisconnectAsync();
        public Task<double> GetFeeAsync();
        public Task OptionsAsync(CancellationToken token);

        public string[] TableHeaders();
        public void DifficultyFound(DifficultyInfo info);
    }
}
