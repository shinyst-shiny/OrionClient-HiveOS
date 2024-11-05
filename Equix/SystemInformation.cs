using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.System.SystemInformation;
using static System.Net.Mime.MediaTypeNames;

namespace Equix
{
    public unsafe static class SystemInformation
    {
        public static (int phsyical, int logical) GetCoreInformation()
        {
            if (OperatingSystem.IsWindows())
            {
                uint length = 0;
                PInvoke.GetLogicalProcessorInformation(null, ref length);

                SYSTEM_LOGICAL_PROCESSOR_INFORMATION[] pCoreInfo = new SYSTEM_LOGICAL_PROCESSOR_INFORMATION[length / sizeof(SYSTEM_LOGICAL_PROCESSOR_INFORMATION)];

                fixed(SYSTEM_LOGICAL_PROCESSOR_INFORMATION* c = pCoreInfo)
                {
                    if (PInvoke.GetLogicalProcessorInformation(c, ref length))
                    {
                        int physicalCores = 0;
                        for (int i = 0; i < pCoreInfo.Length; ++i)
                        {
                            ref SYSTEM_LOGICAL_PROCESSOR_INFORMATION info = ref pCoreInfo[i];

                            if (info.Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                            {
                                ++physicalCores;
                            }
                        }

                        physicalCores = physicalCores == 0 ? Environment.ProcessorCount / 2 : physicalCores;

                        return (physicalCores, Environment.ProcessorCount);
                    }
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                int physicalCores = Environment.ProcessorCount / 2;
                int logicalCores = Environment.ProcessorCount;

                try
                {
                    string[] lines = File.ReadAllLines("/proc/cpuinfo");
                    Regex physicalIdRegex = new Regex(@"^physical id\s+:\s+(\d+)");
                    Regex logicalCoresRegex = new Regex(@"^siblings\s+:\s+(.+)");

                    foreach(var line in lines)
                    {
                        Match physicalCoreMatch = physicalIdRegex.Match(line);
                        Match logicalCoreMatch = logicalCoresRegex.Match(line);

                        if (physicalCoreMatch.Success && int.TryParse(physicalCoreMatch.Groups[1].Value, out int c))
                        {
                            physicalCores = c;
                        }

                        if (logicalCoreMatch.Success && int.TryParse(physicalCoreMatch.Groups[1].Value, out c))
                        {
                            logicalCores = c;
                        }
                    }

                    return (physicalCores, logicalCores);

                }
                catch
                {
                    Console.WriteLine("Here for testing");

                    return (Environment.ProcessorCount / 2, Environment.ProcessorCount);
                }
            }

            return (Environment.ProcessorCount / 2, Environment.ProcessorCount);
        }
    }
}
