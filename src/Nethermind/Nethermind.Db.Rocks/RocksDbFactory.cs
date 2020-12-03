//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Core;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.Db.Rocks
{
    public class RocksDbFactory : IRocksDbFactory
    {
        private readonly IDbConfig _dbConfig;
        private readonly ILogManager _logManager;
        private readonly string _basePath;
        private readonly DisposableStack _disposeStack;
        public RocksDbFactory(IDbConfig dbConfig, ILogManager logManager, string basePath, DisposableStack disposeStack)
        {
            _dbConfig = dbConfig;
            _logManager = logManager;
            _basePath = basePath;
            _disposeStack = disposeStack;
        }

        public IDb CreateDb(RocksDbSettings rocksDbSettings)
        { 
            var db = new SimpleRocksDb(_basePath,
                rocksDbSettings,
                _dbConfig,
                _logManager);
            _disposeStack.Push(db);
            return db;
        }

        public ISnapshotableDb CreateSnapshotableDb(RocksDbSettings rocksDbSettings)
        {
            var rocksDb = CreateDb(rocksDbSettings);
            var stateDb = new StateDb(CreateDb(rocksDbSettings));
            _disposeStack.Push(stateDb);
            _disposeStack.Push(rocksDb);
            return stateDb;
        }

        public IColumnsDb<T> CreateColumnsDb<T>(RocksDbSettings rocksDbSettings)
        {
            var rocksDb = new SimpleColumnRocksDb<T>(_basePath,
                rocksDbSettings,
                _dbConfig,
                _logManager);
            _disposeStack.Push(rocksDb);
            return rocksDb;
        }
    }
}
