using CommandLine;
using CommandLine.Text;
using OrionClientLib.Modules.SettingsData;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClient.Commands
{
    [Verb("mine", HelpText = "Automatically start mining module.")]
    internal class MineCommandLineOptions : CommandLineOptions
    {
    }
}
