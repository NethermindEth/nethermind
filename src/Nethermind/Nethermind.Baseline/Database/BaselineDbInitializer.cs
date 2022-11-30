// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            : base(dbProvider, rocksDbFactory, memDbFactory)
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
