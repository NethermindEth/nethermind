// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;

using Microsoft.Win32.SafeHandles;

namespace Nethermind.Db.Blooms
{
    public class FixedSizeFileStore : IFileStore
    {
        private readonly string _path;
        private readonly int _elementSize;
        private readonly SafeFileHandle _file;

        public FixedSizeFileStore(string path, int elementSize)
        {
            _path = path;
            _elementSize = elementSize;
            _file = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        }

        public void Write(long index, ReadOnlySpan<byte> element)
        {
            if (element.Length != _elementSize)
            {
                throw new ArgumentException($"Element size incorrect. Only elements of length {_elementSize} are acceptable.");
            }

            try
            {
                RandomAccess.Write(_file, element, GetPosition(index));
            }
            catch (ArgumentOutOfRangeException e)
            {
                long position = GetPosition(index);
                throw new InvalidOperationException($"Bloom storage tried to write a file that is too big for file system. " +
                                                    $"Trying to write data at index {index} with size {_elementSize} at file position {position} to file {_path}", e)
                {
                    Data =
                    {
                        {"Index", index},
                        {"Size", _elementSize},
                        {"Position", position},
                        {"Path", _path}
                    }
                };
            }
        }

        public int Read(long index, Span<byte> element)
        {
            return RandomAccess.Read(_file, element, GetPosition(index));
        }

        public IFileReader CreateFileReader()
        {
            return new FileReader(_path, _elementSize);
        }

        private long GetPosition(long index) => index * _elementSize;

        public void Dispose()
        {
            _file.Dispose();
        }
    }
}
