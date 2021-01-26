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

using System.IO;
using Nethermind.Logging;

namespace Nethermind.Db.Blooms
{
    public class FixedSizeFileStoreFactory : IFileStoreFactory
    {
        private readonly string _basePath;
        private readonly string _extension;
        private readonly int _elementSize;

        public FixedSizeFileStoreFactory(string basePath, string extension, int elementSize)
        {
            _basePath = string.Empty.GetApplicationResourcePath(basePath);
            _extension = extension;
            _elementSize = elementSize;
            Directory.CreateDirectory(_basePath);
        }

        public IFileStore Create(string name) => new FixedSizeFileStore(Path.Combine(_basePath, name + "." + _extension), _elementSize);
    }
}
