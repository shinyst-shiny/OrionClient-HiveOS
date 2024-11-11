using OrionClientLib.Modules.Models;
using OrionClientLib.Pools;
using Solnet.Wallet;
using Spectre.Console;
using Spectre.Console.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules
{
    public class PoolModule : IModule
    {
        public string Name { get; } = "Pool";

        private Data _currentData;
        private CancellationTokenSource _cts;

        public async Task<ExecuteResult> ExecuteAsync(Data data)
        {
            _currentData = data;

            return new ExecuteResult { Exited = true };
        }

        public async Task ExitAsync()
        {
            _cts.Cancel();
        }

        public async Task<(bool, string)> InitializeAsync(Data data)
        {
            _currentData = data;
            _cts = new CancellationTokenSource();

            IPool pool = _currentData.GetChosenPool();

            if (pool == null)
            {
                return (false, $"No pool is selected");
            }

            (Wallet wallet, string publicKey) = await _currentData.Settings.GetWalletAsync();

            if (pool.RequiresKeypair && wallet == null)
            {
                return (false, $"A full keypair is required for this pool. Private keys are never sent to the server");
            }

            await pool.OptionsAsync(_cts.Token);

            return (true, String.Empty);
        }
    }
}
