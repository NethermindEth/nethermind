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
