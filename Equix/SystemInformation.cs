using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.System.SystemInformation;

namespace Equix
{
    public unsafe static class SystemInformation
    {
        public static List<CoreInfo> GetCoreInformation()
        {
            List<CoreInfo> coreInfo = new List<CoreInfo>();

            if (OperatingSystem.IsOSPlatformVersionAtLeast("windows", 6, 0, 6000))
            {
                uint length = 0;
                PInvoke.GetLogicalProcessorInformation(null, ref length);

                SYSTEM_LOGICAL_PROCESSOR_INFORMATION[] pCoreInfo = new SYSTEM_LOGICAL_PROCESSOR_INFORMATION[length / sizeof(SYSTEM_LOGICAL_PROCESSOR_INFORMATION)];

                fixed(SYSTEM_LOGICAL_PROCESSOR_INFORMATION* c = pCoreInfo)
                {
                    if (PInvoke.GetLogicalProcessorInformation(c, ref length))
                    {
                        bool logicalFound = false;

                        for (int i = 0; i < pCoreInfo.Length; ++i)
                        {
                            ref SYSTEM_LOGICAL_PROCESSOR_INFORMATION info = ref pCoreInfo[i];

                            if (info.Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                            {
                                double log = Math.Log2(info.ProcessorMask);
                                bool hasLogical = false;

                                if (log != Math.Floor(log))
                                {
                                    log = Math.Floor(log) - 1;
                                    hasLogical = true;
                                    logicalFound |= hasLogical;
                                }

                                ulong physicalMask = 1ul << (int)log;

                                //These checks won't be able to find efficiency cores when hyperthreading is disabled for the performance cores
                                CoreInfo core = new CoreInfo
                                {
                                    PhysicalMask = info.ProcessorMask & physicalMask,
                                    LogicalMask = info.ProcessorMask & (physicalMask << 1),
                                    IsPCore = !(logicalFound && !hasLogical)
                                };

                                coreInfo.Add(core);
                            }
                        }

                        return (coreInfo);
                    }
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                try
                {
                    string[] lines = File.ReadAllLines("/proc/cpuinfo");
                    Regex processorRegex = new Regex(@"^processor\s+:\s+(\d+)");
                    Regex coreIdRegex = new Regex(@"^core id\s+:\s+(.+)");

                    int currentProcessor = -1;
                    List<(int processor, int coreId)> info = new List<(int processor, int coreId)>();

                    foreach (var line in lines)
                    {
                        Match processorMatch = processorRegex.Match(line);
                        Match coreIdMatch = coreIdRegex.Match(line);

                        if (processorMatch.Success && int.TryParse(processorMatch.Groups[1].Value, out int c))
                        {
                            currentProcessor = c;
                        }

                        if (coreIdMatch.Success && int.TryParse(coreIdMatch.Groups[1].Value, out int id))
                        {
                            info.Add((currentProcessor, id));

                            currentProcessor = -1;
                        }

                    }

                    bool logicalFound = false;

                    foreach (var g in info.GroupBy(x => x.coreId))
                    {
                        var physical = g.First();
                        var logical = g.Last();

                        bool hasLogical = physical != logical;
                        logicalFound |= hasLogical;

                        coreInfo.Add(new CoreInfo
                        {
                            PhysicalMask = 1ul << physical.processor,
                            LogicalMask = hasLogical ? 1ul << logical.processor : 0,
                            IsPCore = !(logicalFound && !hasLogical)
                        });
                    }

                    return coreInfo;

                }
                catch
                {
                    Console.WriteLine("Here for testing");

                    return coreInfo;
                }
            }

            return (coreInfo);
        }
    }

    public class CoreInfo
    {
        public ulong PhysicalMask { get; set; }
        public ulong LogicalMask { get; set; }
        public bool IsPCore { get; set; } = true;
        public bool HasLogical => LogicalMask > 0;
        public ulong FullMask => PhysicalMask | LogicalMask;

        public int ThreadCount => HasLogical ? 2 : 1;
    }
}
