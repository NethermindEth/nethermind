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

using System.IO;
using System.IO.Abstractions;
using System.Linq;

namespace Nethermind.Db.FullPruning
{
    public class FullPruningInnerDbFactory : IRocksDbFactory
    {
        private readonly IRocksDbFactory _rocksDbFactory;
        private readonly IFileSystem _fileSystem;
        private int _index;

        public FullPruningInnerDbFactory(IRocksDbFactory rocksDbFactory, IFileSystem fileSystem, string path)
        {
            _rocksDbFactory = rocksDbFactory;
            _fileSystem = fileSystem;
            _index = GetStartingIndex(path);
        }

        public IDb CreateDb(RocksDbSettings rocksDbSettings)
        {
            RocksDbSettings settings = GetRocksDbSettings(rocksDbSettings);
            return _rocksDbFactory.CreateDb(settings);
        }

        public IColumnsDb<T> CreateColumnsDb<T>(RocksDbSettings rocksDbSettings) where T : notnull
        {
            RocksDbSettings settings = GetRocksDbSettings(rocksDbSettings);
            return _rocksDbFactory.CreateColumnsDb<T>(settings);
        }
        
        private RocksDbSettings GetRocksDbSettings(RocksDbSettings rocksDbSettings)
        {
            _index++;
            bool firstDb = _index == -1;
            string dbName = firstDb ? rocksDbSettings.DbName : rocksDbSettings.DbName + _index;
            string dbPath = firstDb ? rocksDbSettings.DbPath : _fileSystem.Path.Combine(rocksDbSettings.DbPath, _index.ToString());
            return rocksDbSettings.Clone(dbName, dbPath);
        }

        private int GetStartingIndex(string path)
        {
            IDirectoryInfo directory = _fileSystem.DirectoryInfo.FromDirectoryName(path);
            if (directory.Exists)
            {
                if (!directory.EnumerateFiles().Any())
                {
                    int minIndex = directory.EnumerateDirectories()
                        .Select(d => int.TryParse(d.Name, out int index) ? index : -1)
                        .Where(i => i >= 0)
                        .OrderBy(i => i)
                        .FirstOrDefault();

                    return minIndex - 1;
                }

                return -2;
            }

            return -1;
        }
    }
}
