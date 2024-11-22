using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Utilities
{
    internal static class Utils
    {
        public static string GetExecutableDirectory()
        {
            string strExeFilePath = AppContext.BaseDirectory;

            return Path.GetDirectoryName(strExeFilePath) ?? String.Empty;
        }

        public static string GetRoot()
        {
            return Directory.GetDirectoryRoot(System.Reflection.Assembly.GetExecutingAssembly().Location);
        }
    }
}
