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

namespace Nethermind.Baseline.Database
{
    public interface IBaselineDbInitializer
    {
        Task Init();
    }

    public class BaselineDbConsts
    {
        public const string BaselineTreeDbName = "BaselineTree";
        public const string BaselineTreeMetadataDbName = "BaselineTreeMetadata";
        public const string BaselineTreeDbPath = "baselineTree";
        public const string BaselineTreeMetadataDbPath = "baselineTreeMetadata";
    }

    public class BaselineDbInitializer : IBaselineDbInitializer
    {

        private readonly IDbProvider _dbProvider;
        private readonly IBaselineConfig _baselineConfig;
        private readonly IRocksDbFactory _rocksDbFactory;
        private readonly IMemDbFactory _memDbFactory;
        public BaselineDbInitializer(
            IDbProvider dbProvider,
            IBaselineConfig baselineConfig,
            IRocksDbFactory rocksDbFactory,
            IMemDbFactory memDbFactory)
        {
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _baselineConfig = baselineConfig ?? throw new ArgumentNullException(nameof(baselineConfig));
            _rocksDbFactory = rocksDbFactory ?? throw new ArgumentNullException(nameof(rocksDbFactory));
            _memDbFactory = memDbFactory ?? throw new ArgumentNullException(nameof(memDbFactory));
        }
        public async Task Init()
        {
            HashSet<Task> allInitializers = new HashSet<Task>();
            allInitializers.Add(Task.Run(() =>
            {
                IDb baselineTreeDb;
                if (_dbProvider.DbMode == DbModeHint.Persisted)
                    baselineTreeDb = _rocksDbFactory.CreateDb(new RocksDbSettings()
                    {
                        DbName = BaselineDbConsts.BaselineTreeDbName,
                        DbPath = BaselineDbConsts.BaselineTreeDbPath,

                        CacheIndexAndFilterBlocks = _baselineConfig.BaselineTreeDbCacheIndexAndFilterBlocks,
                        BlockCacheSize = _baselineConfig.BaselineTreeDbBlockCacheSize,
                        WriteBufferNumber = _baselineConfig.BaselineTreeDbWriteBufferNumber,
                        WriteBufferSize = _baselineConfig.BaselineTreeDbWriteBufferSize,

                        UpdateReadMetrics = () => Metrics.BaselineTreeDbReads++,
                        UpdateWriteMetrics = () => Metrics.BaselineTreeDbWrites++,
                    });
                else
                    baselineTreeDb = _memDbFactory.CreateDb(BaselineDbConsts.BaselineTreeDbName);
                _dbProvider.RegisterDb(BaselineDbConsts.BaselineTreeDbName, baselineTreeDb);
            }));
            allInitializers.Add(Task.Run(() =>
            {
                IDb baselineMetadataDb;
                if (_dbProvider.DbMode == DbModeHint.Persisted)
                    baselineMetadataDb = _rocksDbFactory.CreateDb(new RocksDbSettings()
                    {
                        DbName = BaselineDbConsts.BaselineTreeMetadataDbName,
                        DbPath = BaselineDbConsts.BaselineTreeMetadataDbPath,

                        CacheIndexAndFilterBlocks = _baselineConfig.BaselineTreeDbCacheIndexAndFilterBlocks,
                        BlockCacheSize = _baselineConfig.BaselineTreeDbBlockCacheSize,
                        WriteBufferNumber = _baselineConfig.BaselineTreeDbWriteBufferNumber,
                        WriteBufferSize = _baselineConfig.BaselineTreeDbWriteBufferSize,

                        UpdateReadMetrics = () => Metrics.BaselineTreeMetadataDbReads++,
                        UpdateWriteMetrics = () => Metrics.BaselineTreeMetadataDbWrites++,
                    });
                else
                    baselineMetadataDb = _memDbFactory.CreateDb(BaselineDbConsts.BaselineTreeMetadataDbName);
                _dbProvider.RegisterDb(BaselineDbConsts.BaselineTreeMetadataDbName, baselineMetadataDb);
            }));
            await Task.WhenAll(allInitializers);
        }
    }
}
