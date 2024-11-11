using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OrionClientLib.Modules.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace OrionClientLib.Modules
{
    public interface IModule
    {
        public string Name { get; }

        public Task<(bool success, string errorMessage)> InitializeAsync(Data data);
        public Task<ExecuteResult> ExecuteAsync(Data data);
        public Task ExitAsync();
    }
}
