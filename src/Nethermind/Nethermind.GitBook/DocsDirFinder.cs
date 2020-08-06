using System;
using System.IO;
using System.Linq;

namespace Nethermind.GitBook
{
    public static class DocsDirFinder
    {
        public static string FindJsonRpc()
        {
            string currentDir = Environment.CurrentDirectory;
            do
            {
                if (currentDir == null)
                {
                    return null;
                }

                if (Directory.GetDirectories(currentDir).Contains(Path.Combine(currentDir, "gitbook")))
                {
                    char pathSeparator = Path.AltDirectorySeparatorChar;
                    return Path.Combine(currentDir, $"gitbook{pathSeparator}docs{pathSeparator}nethermind-utilities{pathSeparator}json-rpc");
                }

                currentDir = new DirectoryInfo(currentDir).Parent?.FullName;
            } while (true);
        }
    }
}