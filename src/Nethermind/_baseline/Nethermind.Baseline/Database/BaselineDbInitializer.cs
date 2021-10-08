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
using System.Threading.Tasks;
using Nethermind.Baseline.Config;
using Nethermind.Db;

namespace Nethermind.Baseline.Database
{
    public class BaselineDbNames
    {
        public const string BaselineTree = "baselineTree";
        public const string BaselineTreeMetadata = "baselineTreeMetadata";
    }

    public class BaselineDbInitializer : RocksDbInitializer
    {
        private readonly IBaselineConfig _baselineConfig;
        public BaselineDbInitializer(
            IDbProvider dbProvider,
            IBaselineConfig baselineConfig,
            IRocksDbFactory rocksDbFactory,
            IMemDbFactory memDbFactory)
            : base (dbProvider, rocksDbFactory, memDbFactory)
        {
            _baselineConfig = baselineConfig ?? throw new ArgumentNullException(nameof(baselineConfig));
        }
        public async Task Init()
        {
            RegisterDb(
                new RocksDbSettings(
                    GetTitleDbName(BaselineDbNames.BaselineTree), BaselineDbNames.BaselineTree)
            {
                CacheIndexAndFilterBlocks = _baselineConfig.BaselineTreeDbCacheIndexAndFilterBlocks,
                BlockCacheSize = _baselineConfig.BaselineTreeDbBlockCacheSize,
                WriteBufferNumber = _baselineConfig.BaselineTreeDbWriteBufferNumber,
                WriteBufferSize = _baselineConfig.BaselineTreeDbWriteBufferSize,

                UpdateReadMetrics = () => Metrics.BaselineTreeDbReads++,
                UpdateWriteMetrics = () => Metrics.BaselineTreeDbWrites++,
            });
            RegisterDb(
                new RocksDbSettings(
                    GetTitleDbName(BaselineDbNames.BaselineTreeMetadata), BaselineDbNames.BaselineTreeMetadata)
            {
                CacheIndexAndFilterBlocks = _baselineConfig.BaselineTreeMetadataDbCacheIndexAndFilterBlocks,
                BlockCacheSize = _baselineConfig.BaselineTreeMetadataDbBlockCacheSize,
                WriteBufferNumber = _baselineConfig.BaselineTreeMetadataDbWriteBufferNumber,
                WriteBufferSize = _baselineConfig.BaselineTreeMetadataDbWriteBufferSize,

                UpdateReadMetrics = () => Metrics.BaselineTreeMetadataDbReads++,
                UpdateWriteMetrics = () => Metrics.BaselineTreeMetadataDbWrites++,
            });

            await InitAllAsync();
        }
    }
}
