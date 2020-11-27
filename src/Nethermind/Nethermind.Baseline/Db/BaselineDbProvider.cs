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
using Nethermind.Baseline.Config;
using Nethermind.Db;
using Nethermind.Db.Rocks.Config;

namespace Nethermind.Baseline.Db
{
    public interface IBaselineDbProvider
    {
        public IDb? BaselineTreeDb { get; }

        public IDb? BaselineTreeMetadataDb { get; }

        Task Init();
    }

    public class BaselineDbProvider : IBaselineDbProvider
    {
        private const string BaselineTreeDbName = "BaselineTree";
        private const string BaselineTreeMetadataDbName = "BaselineTreeMetadata";
        private const string BaselineTreeDbPath = "baselineTree";
        private const string BaselineTreeMetadataDbPath = "baselineTreeMetadata";

        private readonly IDbProvider _dbProvider;
        private readonly IBaselineConfig _baselineConfig;
        private readonly IDbConfig _dbConfig;
        private readonly IRocksDbFactory _rocksDbFactory;
        private readonly IMemDbFactory _memDbFactory;
        public BaselineDbProvider(
            IDbProvider dbProvider,
            IBaselineConfig baselineConfig,
            IDbConfig dbConfig,
            IRocksDbFactory rocksDbFactory,
            IMemDbFactory memDbFactory)
        {
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _baselineConfig = baselineConfig ?? throw new ArgumentNullException(nameof(baselineConfig));
            _dbConfig = dbConfig ?? throw new ArgumentNullException(nameof(dbConfig));
            _rocksDbFactory = rocksDbFactory ?? throw new ArgumentNullException(nameof(rocksDbFactory));
            _memDbFactory = memDbFactory ?? throw new ArgumentNullException(nameof(memDbFactory));
        }
        public async Task Init()
        {
            var baselineDbConfig = new BaselineDbConfig(_baselineConfig, _dbConfig);
            HashSet<Task> allInitializers = new HashSet<Task>();
            allInitializers.Add(Task.Run(() =>
            {
                if (_dbProvider.DbMode == DbModeHint.Persisted)
                    BaselineTreeDb = _rocksDbFactory.CreateDb(new RocksDbSettings()
                    {
                        DbName = BaselineTreeDbName,
                        DbPath = BaselineTreeDbPath
                    });
                else
                    BaselineTreeDb = _memDbFactory.CreateDb(BaselineTreeDbName);
            }));
            allInitializers.Add(Task.Run(() =>
            {
                if (_dbProvider.DbMode == DbModeHint.Persisted)
                    BaselineTreeMetadataDb = _rocksDbFactory.CreateDb(new RocksDbSettings()
                    {
                        DbName = BaselineTreeMetadataDbName,
                        DbPath = BaselineTreeMetadataDbPath
                    });
                else
                    BaselineTreeMetadataDb = _memDbFactory.CreateDb(BaselineTreeMetadataDbName);
            }));
            await Task.WhenAll(allInitializers);
        }
        public IDb? BaselineTreeDb { get; private set; }

        public IDb? BaselineTreeMetadataDb { get; private set; }
    }
}
