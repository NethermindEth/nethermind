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
// 

using System;
using System.IO;

namespace Nethermind.Core.Test.IO
{
    public class TempPath : IDisposable
    {
        private TempPath(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempPath GetTempFile() => new TempPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString()));

        public static TempPath GetTempFile(string subPath) => string.IsNullOrEmpty(subPath)
            ? GetTempFile()
            : new TempPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), subPath));

        public static TempPath GetTempDirectory(string subPath = null) => 
            new TempPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), subPath ?? Guid.NewGuid().ToString()));

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
            else if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }

        public override string ToString() => Path;
    }
}
