// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using System;
using System.Threading.Tasks;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.Db;

namespace Nethermind.DataMarketplace.Infrastructure.Database
{
    public class NdmDbNames
    {
        public const string Configs = "configs";
        public const string EthRequests = "ethRequests";
    }

    public class NdmDbInitializer : RocksDbInitializer
    {
        private readonly INdmConfig _ndmConfig;

        public NdmDbInitializer(
            INdmConfig ndmConfig,
            IDbProvider dbProvider,
            IRocksDbFactory rocksDbFactory,
            IMemDbFactory memDbFactory)
            : base(dbProvider, rocksDbFactory, memDbFactory)
        {
            _ndmConfig = ndmConfig ?? throw new ArgumentNullException(nameof(ndmConfig));
        }

        public async Task Init()
        {
            RegisterDb(new RocksDbSettings(
                GetTitleDbName(NdmDbNames.Configs), NdmDbNames.Configs)
            {
                CacheIndexAndFilterBlocks = _ndmConfig.ConfigsDbCacheIndexAndFilterBlocks,
                BlockCacheSize = _ndmConfig.ConfigsDbBlockCacheSize,
                WriteBufferNumber = _ndmConfig.ConfigsDbWriteBufferNumber,
                WriteBufferSize = _ndmConfig.ConfigsDbWriteBufferSize,
                UpdateReadMetrics = () => Metrics.ConfigsDbReads++,
                UpdateWriteMetrics = () => Metrics.ConfigsDbWrites++,
            });
            RegisterDb(new RocksDbSettings(
                GetTitleDbName(NdmDbNames.EthRequests), NdmDbNames.EthRequests)
            {
                CacheIndexAndFilterBlocks = _ndmConfig.EthRequestsDbCacheIndexAndFilterBlocks,
                BlockCacheSize = _ndmConfig.EthRequestsDbBlockCacheSize,
                WriteBufferNumber = _ndmConfig.EthRequestsDbWriteBufferNumber,
                WriteBufferSize = _ndmConfig.EthRequestsDbWriteBufferSize,
                UpdateReadMetrics = () => Metrics.EthRequestsDbReads++,
                UpdateWriteMetrics = () => Metrics.EthRequestsDbWrites++,
            });

            await InitAllAsync();
        }
    }
}
