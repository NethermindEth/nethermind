// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;

namespace Nethermind.Db.Blooms
{
    public class FileReader : IFileReader
    {
        private readonly int _elementSize;
        private readonly FileStream _file;

        public FileReader(string filePath, int elementSize)
        {
            _elementSize = elementSize;
            _file = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }

        public int Read(long index, Span<byte> element)
        {
            SeekIndex(index);
            return _file.Read(element);
        }

        private void SeekIndex(long index)
        {
            long seekPosition = index * _elementSize;
            if (_file.Position != seekPosition)
            {
                _file.Position = seekPosition;
            }
        }

        public void Dispose()
        {
            _file.Dispose();
        }
    }
}
