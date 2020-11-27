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

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nethermind.Db
{
    public class StandardDbInitializer
    {
        private readonly IDbProvider _dbProvider;
        private readonly IRocksDbFactory _rocksDbFactory;
        private readonly IMemDbFactory _memDbFactory;

        public StandardDbInitializer(IDbProvider dbProvider, IRocksDbFactory rocksDbFactory, IMemDbFactory memDbFactory)
        {
            _dbProvider = dbProvider;
            _rocksDbFactory = rocksDbFactory;
            _memDbFactory = memDbFactory;
        }

        public async Task InitStandardDbs()
        {
            HashSet<Task> allInitializers = new HashSet<Task>();
            allInitializers.Add(RegisterDb(DbNames.Blocks, () => Metrics.BlocksDbReads++, () => Metrics.BlocksDbWrites++));
            allInitializers.Add(RegisterDb(DbNames.Headers, () => Metrics.HeaderDbReads++, () => Metrics.HeaderDbWrites++));
            allInitializers.Add(RegisterDb(DbNames.BlockInfos, () => Metrics.BlockInfosDbReads++, () => Metrics.BlockInfosDbWrites++));
            allInitializers.Add(RegisterDb(DbNames.State, () => Metrics.StateDbReads++, () => Metrics.StateDbWrites++, snapshotDb: true));
            allInitializers.Add(RegisterDb(DbNames.Code, () => Metrics.CodeDbReads++, () => Metrics.CodeDbWrites++, snapshotDb: true));
            allInitializers.Add(RegisterDb(DbNames.PendingTxs, () => Metrics.PendingTxsDbReads++, () => Metrics.PendingTxsDbWrites++));
            allInitializers.Add(RegisterDb(DbNames.Bloom, () => Metrics.BloomDbReads++, () => Metrics.BloomDbWrites++));
            allInitializers.Add(RegisterDb(DbNames.CHT, () => Metrics.CHTDbReads++, () => Metrics.CHTDbWrites++));

            await Task.WhenAll(allInitializers);
        }

        private Task RegisterDb(string dbName, Action updateReadsMetrics, Action updateWriteMetrics, bool snapshotDb = false)
        {
            return Task.Run(() =>
            {
                IDb db;
                if (snapshotDb)
                    db = CreateSnapshotableDb(dbName, updateReadsMetrics, updateWriteMetrics);
                else
                    db = CreateDb(dbName, updateReadsMetrics, updateWriteMetrics);

                _dbProvider.RegisterDb(dbName, db);
            });
        }

        private IDb CreateDb(string dbName, Action updateReadsMetrics, Action updateWriteMetrics)
        {
            if (_dbProvider.DbMode == DbModeHint.Persisted)
            {
                var settings = GetRocksDbSettings(dbName, updateReadsMetrics, updateWriteMetrics);
                return _rocksDbFactory.CreateDb(settings);
            }

            return _memDbFactory.CreateDb(dbName);
        }

        private IDb CreateSnapshotableDb(string dbName, Action updateReadsMetrics, Action updateWriteMetrics)
        {
            if (_dbProvider.DbMode == DbModeHint.Persisted)
            {
                var settings = GetRocksDbSettings(dbName, updateReadsMetrics, updateWriteMetrics);
                return _rocksDbFactory.CreateSnapshotableDb(settings);
            }

            return _memDbFactory.CreateSnapshotableDb(dbName);
        }

        private RocksDbSettings GetRocksDbSettings(string dbName, Action updateReadsMetrics, Action updateWriteMetrics)
        {
            return new RocksDbSettings()
            {
                DbName = UppercaseFirst(dbName),
                DbPath = dbName,
                UpdateReadMetrics = updateReadsMetrics,
                UpdateWriteMetrics = updateWriteMetrics
            };
        }

        private string UppercaseFirst(string str)
        {
            return char.ToUpper(str[0]) + str.Substring(1);
        }
    }
}
