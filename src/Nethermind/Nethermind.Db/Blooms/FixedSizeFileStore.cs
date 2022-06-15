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
using System.IO;
using System.Threading;

namespace Nethermind.Db.Blooms
{
    public class FixedSizeFileStore : IFileStore
    {
        private readonly string _path;
        private readonly int _elementSize;
        private readonly Stream _fileWrite;
        private readonly Stream _fileRead;
        private int _needsFlush;
        
        public FixedSizeFileStore(string path, int elementSize)
        {
            _path = path;
            _elementSize = elementSize;
            _fileWrite = File.Open(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            _fileRead = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }

        public void Write(long index, ReadOnlySpan<byte> element)
        {
            if (element.Length != _elementSize)
            {
                throw new ArgumentException($"Element size incorrect. Only elements of length {_elementSize} are acceptable.");
            }

            try
            {
                lock (_fileWrite)
                {
                    SeekIndex(_fileWrite, index);
                    _fileWrite.Write(element);
                    Interlocked.Exchange(ref _needsFlush, 1);
                }
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
            EnsureFlushed();
            
            lock (_fileRead)
            {
                SeekIndex(_fileRead, index);
                return _fileRead.Read(element);
            }            
        }

        public IFileReader CreateFileReader()
        {
            EnsureFlushed();
            return new FileReader(_path, _elementSize);
        }

        private void EnsureFlushed()
        {
            if (Interlocked.CompareExchange(ref _needsFlush, 0, 1) == 1)
            {
                lock (_fileWrite)
                {
                    _fileWrite.Flush();
                }
            }
        }

        private void SeekIndex(Stream file, long index)
        {
            long seekPosition = GetPosition(index);
            if (file.Position != seekPosition)
            {
                file.Position = seekPosition;
            }
        }

        private long GetPosition(long index) => index * _elementSize;

        public void Dispose()
        {
            _fileWrite.Dispose();
            _fileRead.Dispose();
        }
    }
}
