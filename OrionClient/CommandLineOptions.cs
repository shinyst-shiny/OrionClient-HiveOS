using CommandLine;
using CommandLine.Text;
using OrionClientLib.Modules.SettingsData;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClient
{
    internal class CommandLineOptions
    {
        [Option("gpu", HelpText = "Enables the gpu using selected GPUs or all supported if none are selected")]
        public bool EnableGPU { get; set; }

        [Option("opencl", HelpText = "Uses OpenCL devices rather than Cuda", Hidden = true)]
        public bool OpenCL { get; set; }

        [Option("disable-cpu", HelpText = "Disables the cpu")]
        public bool DisableCPU { get; set; }

        [Option('k', "keypair", HelpText = "Keypair file path")]
        public string? KeyFile { get; set; }

        [Option("key", HelpText = "Public key for pools that don't require a keypair")]
        public string? PublicKey { get; set; }

        #region CPU Settings

        [Option('t', "cpu-threads", HelpText = "Total threads to use for mining (0 = all threads)")]
        public int? CPUThreads { get; set; }

        #endregion

        #region GPU Settings

        [Option("gpu-batch-size", HelpText = "Higher values use more ram and take longer to run. Lower values can cause lower hashrates")]
        public int? BatchSize { get; set; }
        [Option("gpu-block-size", HelpText = "GPU block size")]
        public int? BlockSize { get; set; }
        [Option("gpu-gen-threads", HelpText = "CPU threads to use to generation program for GPU")]
        public int? ProgramGenerationThreads { get; set; }

        #endregion
    }
}
