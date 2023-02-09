// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;

namespace Nethermind.GitBook
{
    public static class DocsDirFinder
    {
        public static string FindDocsDir()
        {
            string currentDir = Environment.CurrentDirectory;
            do
            {
                if (currentDir == null)
                {
                    return null;
                }

                var dir = Directory
                    .EnumerateDirectories(currentDir, "docs", SearchOption.TopDirectoryOnly)
                    .SingleOrDefault();

                if (dir != null)
                {
                    return dir;
                }

                currentDir = Directory.GetParent(currentDir)?.FullName;
            } while (true);
        }

        public static string FindRunnerDir()
        {
            string currentDir = Environment.CurrentDirectory;
            do
            {
                if (currentDir == null)
                {
                    return null;
                }

                if (Directory.GetDirectories(currentDir).Contains(Path.Combine(currentDir, "Nethermind.Runner")))
                {
                    return Path.Combine(currentDir, "Nethermind.Runner");
                }

                currentDir = new DirectoryInfo(currentDir).Parent?.FullName;
            } while (true);
        }
    }
}
