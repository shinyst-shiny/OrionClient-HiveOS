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
    [Flags]
    public enum Coin { Ore, Coal };

    public interface IPool
    {
        public string PoolName { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public Dictionary<string, string> Features { get; }
        public bool HideOnPoolList { get; }
        public Coin Coins { get; }
        public bool RequiresKeypair { get; }

        public event EventHandler<NewChallengeInfo> OnChallengeUpdate;
        public event EventHandler<string[]> OnMinerUpdate;
        public event EventHandler PauseMining;
        public event EventHandler ResumeMining;

        public void SetWalletInfo(Wallet wallet, string publicKey);
        public Task<(bool success, string errorMessage)> SetupAsync(CancellationToken token, bool initialSetup = false);

        public Task<bool> ConnectAsync(CancellationToken token);
        public Task<bool> DisconnectAsync();
        public Task<double> GetFeeAsync(CancellationToken token);
        public Task OptionsAsync(CancellationToken token);

        public string[] TableHeaders();
        public void DifficultyFound(DifficultyInfo info);
    }
}
