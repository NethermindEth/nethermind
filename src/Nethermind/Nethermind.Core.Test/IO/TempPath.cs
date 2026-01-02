// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        public static TempPath GetTempFile() => new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString()));

        public static TempPath GetTempFile(string subPath) => string.IsNullOrEmpty(subPath)
            ? GetTempFile()
            : new TempPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), subPath));

        public static TempPath GetTempDirectory(string? subPath = null) =>
            new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), subPath ?? Guid.NewGuid().ToString()));

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
