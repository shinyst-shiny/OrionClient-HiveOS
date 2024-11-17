using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Hashers.GPU
{
    class IntrinsicMethodAttribute : Attribute
    {
        public string GenerateMethod { get; private set; }
        public string OpenCLMethod { get; private set; }
        public bool IsOpenCLRedirect { get; private set; }

        public IntrinsicMethodAttribute(string generateMethod, string openClMethod = null, bool isOpenCLRedirect = false)
        {
            GenerateMethod = generateMethod;
            OpenCLMethod = openClMethod;
            IsOpenCLRedirect = isOpenCLRedirect;
        }
    }
}
