// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Nethermind.Logging
{
    public static class PathUtils
    {
        public static string ExecutingDirectory { get; }

        static PathUtils()
        {
            Process process = Process.GetCurrentProcess();
            if (process.ProcessName.StartsWith("dotnet", StringComparison.InvariantCultureIgnoreCase)
                || process.ProcessName.Equals("ReSharperTestRunner", StringComparison.InvariantCultureIgnoreCase))
            {
                ExecutingDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }
            else
            {
                ExecutingDirectory = Path.GetDirectoryName(process.MainModule.FileName);
                Console.WriteLine($"Resolved executing directory as {ExecutingDirectory}.");
            }
        }

        public static string GetApplicationResourcePath(this string resourcePath, string overridePrefixPath = null)
        {
            if (string.IsNullOrWhiteSpace(resourcePath))
            {
                resourcePath = string.Empty;
            }

            if (Path.IsPathRooted(resourcePath) || IsExplicitlyRelative(resourcePath))
            {
                return resourcePath;
            }

            if (string.IsNullOrEmpty(overridePrefixPath))
            {
                return Path.Combine(ExecutingDirectory, resourcePath);
            }

            return Path.IsPathRooted(overridePrefixPath) || IsExplicitlyRelative(overridePrefixPath)
                ? Path.Combine(overridePrefixPath, resourcePath)
                : Path.Combine(ExecutingDirectory, overridePrefixPath, resourcePath);
        }

        static readonly string[] RelativePrefixes = new[]
        {
            "." + Path.DirectorySeparatorChar,
            "." + Path.AltDirectorySeparatorChar,
            ".." + Path.DirectorySeparatorChar,
            ".." + Path.AltDirectorySeparatorChar,
        }.Distinct().ToArray();

        public static bool IsExplicitlyRelative(string resourcePath) => RelativePrefixes.Any(resourcePath.StartsWith);
    }
}
