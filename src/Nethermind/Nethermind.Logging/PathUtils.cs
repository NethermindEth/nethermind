//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
            var process = Process.GetCurrentProcess();
            if (process.ProcessName.StartsWith("dotnet", StringComparison.InvariantCultureIgnoreCase))
            {
                ExecutingDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return;
            }

            ExecutingDirectory = Path.GetDirectoryName(process.MainModule.FileName);
            Console.WriteLine($"Resolved executing directory as {ExecutingDirectory}.");
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
        
        static readonly string[] RelativePrefixes = new []
        {
            "." + Path.DirectorySeparatorChar,
            "." + Path.AltDirectorySeparatorChar,
            ".." + Path.DirectorySeparatorChar,
            ".." + Path.AltDirectorySeparatorChar,
        }.Distinct().ToArray();

        public static bool IsExplicitlyRelative(string resourcePath) => RelativePrefixes.Any(resourcePath.StartsWith);
    }
}
