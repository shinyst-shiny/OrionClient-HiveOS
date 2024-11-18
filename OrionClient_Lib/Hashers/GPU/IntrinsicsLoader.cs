using ILGPU.Backends.PTX;
using ILGPU.IR.Intrinsics;
using ILGPU;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using ILGPU.IR;
using ILGPU.Backends.OpenCL;

namespace OrionClientLib.Hashers.GPU
{
    internal class IntrinsicsLoader
    {
        public static void Load(Type t, Context context)
        {
            var methods = t.GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

            foreach (var method in methods)
            {
                if (method.GetCustomAttribute(typeof(IntrinsicMethodAttribute)) is IntrinsicMethodAttribute att)
                {
                    context.GetIntrinsicManager().RegisterMethod(method, new PTXIntrinsic(t, att.GenerateMethod, IntrinsicImplementationMode.GenerateCode));

                    if (!String.IsNullOrEmpty(att.OpenCLMethod))
                    {
                        context.GetIntrinsicManager().RegisterMethod(method, new CLIntrinsic(t, att.GenerateMethod, att.IsOpenCLRedirect ? IntrinsicImplementationMode.Redirect : IntrinsicImplementationMode.GenerateCode));
                    }
                }
            }
        }

        public static void Load<T>(Context context)
        {
            Load(typeof(T), context);
        }
    }
}
