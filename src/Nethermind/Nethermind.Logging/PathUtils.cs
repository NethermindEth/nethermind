/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Nethermind.Logging
{
    public static class PathUtils
    {
        public static string GetExecutingDirectory()
        {
            var process = Process.GetCurrentProcess();
            if (process.ProcessName.StartsWith("dotnet", StringComparison.InvariantCultureIgnoreCase))
            {
                return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }
            
            var filePath = process.MainModule.FileName;
            var fileName = filePath.Split(Path.DirectorySeparatorChar).LastOrDefault();
            var fileNameIndex = filePath.LastIndexOf(fileName, StringComparison.InvariantCultureIgnoreCase);

            return filePath.Substring(0, fileNameIndex);
        }
    }
}
