using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace Nethermind.Core.Utils
{
    public static class PathUtils
    {
        public static string GetExecutingDirectory()
        {
            return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);;
        }
    }
}
