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

namespace Nethermind.Db.FullPruning
{
    public class FullPruningInnerDbFactory : IRocksDbFactory
    {
        private const int EmptyIndex = -2;
        private readonly IRocksDbFactory _rocksDbFactory;
        private int _index = EmptyIndex;

        public FullPruningInnerDbFactory(IRocksDbFactory rocksDbFactory)
        {
            _rocksDbFactory = rocksDbFactory;
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
            if (_index == EmptyIndex)
            {
                _index = GetStartingIndex();
            }
            
            _index++;
            bool firstDb = _index == 0;
            string dbName = firstDb ? rocksDbSettings.DbName : rocksDbSettings.DbName + _index;
            string dbPath = firstDb ? rocksDbSettings.DbPath : Path.Combine(rocksDbSettings.DbPath, _index.ToString());
            return rocksDbSettings.Clone(dbName, dbPath);
        }

        private int GetStartingIndex()
        {
            return -1;
        }
    }
}
