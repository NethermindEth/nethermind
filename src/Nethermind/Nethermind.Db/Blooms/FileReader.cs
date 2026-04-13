// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;

using Microsoft.Win32.SafeHandles;

namespace Nethermind.Db.Blooms
{
    public class FileReader(string filePath, int elementSize) : IFileReader
    {
        private readonly int _elementSize = elementSize;
        private readonly SafeFileHandle _file = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        public int Read(long index, Span<byte> element)
        {
            return RandomAccess.Read(_file, element, GetPosition(index));
        }

        private long GetPosition(long index) => index * _elementSize;

        public void Dispose()
        {
            _file.Dispose();
        }
    }
}
