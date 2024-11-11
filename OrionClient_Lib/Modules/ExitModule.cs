using OrionClientLib.Modules.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules
{
    public class ExitModule : IModule
    {
        public string Name { get; } = "Exit";

        public async Task<ExecuteResult> ExecuteAsync(Data data)
        {
            Environment.Exit(0);

            return new ExecuteResult
            {
                Exited = true
            };
        }

        public async Task ExitAsync()
        {

        }

        public async Task<(bool, string)> InitializeAsync(Data data)
        {
            return (true, String.Empty);
        }
    }
}
