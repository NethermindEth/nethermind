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
// 

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
                GetTitleDbName(NdmDbNames.EthRequests),NdmDbNames.EthRequests)
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
