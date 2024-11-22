using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Tmds.Linux;
using Windows.Win32;
using Windows.Win32.System.Memory;

namespace DrillX.Compiler
{
    internal unsafe class VirtualMemory
    {
        public static void* HashxVmAlloc(nuint bytes)
        {
            void* mem;

            if (OperatingSystem.IsOSPlatformVersionAtLeast("windows", 5, 1, 2600))
            {
                mem = PInvoke.VirtualAlloc(null, bytes, VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT, PAGE_PROTECTION_FLAGS.PAGE_EXECUTE_READWRITE);
            }
            else if (OperatingSystem.IsLinux())
            {
                mem = LibC.mmap(null, (size_t)bytes, LibC.PROT_READ | LibC.PROT_WRITE, LibC.MAP_ANONYMOUS | LibC.MAP_PRIVATE, -1, 0);

                if(mem == LibC.MAP_FAILED)
                {
                    return null;
                }
            }
            else
            {
                throw new Exception("Unknown operating system");
            }

#if IGNORE_SECURITY_RISK
            HashxVmRx(mem, new nuint(X86Compiler.CodeSize));
#endif

            return mem;
        }

        public static int PageProtect(void* ptr, nuint bytes, bool executeRead)
        {
            if (OperatingSystem.IsOSPlatformVersionAtLeast("windows", 5, 1, 2600))
            {
                PAGE_PROTECTION_FLAGS oldp;

                if (!PInvoke.VirtualProtect(ptr, bytes, executeRead ? PAGE_PROTECTION_FLAGS.PAGE_EXECUTE_READWRITE : PAGE_PROTECTION_FLAGS.PAGE_READWRITE, out oldp))
                {
                    return 0;
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                if (LibC.mprotect(ptr, (size_t)bytes, executeRead ? (LibC.PROT_READ | LibC.PROT_EXEC | LibC.PROT_WRITE) : (LibC.PROT_READ | LibC.PROT_WRITE)) == -1)
                {
                    return 0;
                }
            }
            else
            {
                throw new Exception("Unknown operating system");
            }

            return 1;
        }

        public static void HashxVmRw(void* ptr, nuint bytes)
        {
            PageProtect(ptr, bytes, false);
        }

        public static void HashxVmRx(void* ptr, nuint bytes)
        {
            PageProtect(ptr, bytes, true);
        }

        public static void HashxVmFree(void* ptr, nuint bytes)
        {
            if (OperatingSystem.IsOSPlatformVersionAtLeast("windows", 5, 1, 2600))
            {
                PInvoke.VirtualFree(ptr, 0, VIRTUAL_FREE_TYPE.MEM_RELEASE);
            }
            else if (OperatingSystem.IsLinux())
            {
                LibC.munmap(ptr, (size_t)bytes);
            }
            else
            {
                throw new Exception("Unknown operating system");
            }
        }
    }
}
