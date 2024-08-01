// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;

using Microsoft.Win32.SafeHandles;

namespace Nethermind.Db.Blooms
{
    public class FileReader : IFileReader
    {
        private readonly int _elementSize;
        private readonly SafeFileHandle _file;

        public FileReader(string filePath, int elementSize)
        {
            _elementSize = elementSize;
            _file = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }

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
