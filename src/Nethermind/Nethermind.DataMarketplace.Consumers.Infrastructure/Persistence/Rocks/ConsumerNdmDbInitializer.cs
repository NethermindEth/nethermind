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
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.Db;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks
{
    public class ConsumerNdmDbConsts
    {
        public const string ConsumerDepositApprovalsDbName = "ConsumerDepositApprovals";
        public const string ConsumerReceiptsDbName = "ConsumerReceipts";
        public const string ConsumerSessionsDbName = "ConsumerSessions";
        public const string DepositsDbName = "Deposits";
    }

    public class ConsumerNdmDbInitializer : RocksDbInitializer
    {
        private readonly INdmConfig _ndmConfig;
        public ConsumerNdmDbInitializer(
            IDbProvider dbProvider,
            INdmConfig ndmConfig,
            IRocksDbFactory rocksDbFactory,
            IMemDbFactory memDbFactory)
            : base(dbProvider, rocksDbFactory, memDbFactory)
        {
            _ndmConfig = ndmConfig ?? throw new ArgumentNullException(nameof(NdmConfig));
        }
        public void Init()
        {
            RegisterDb(new RocksDbSettings()
            {
                DbName = ConsumerNdmDbConsts.ConsumerDepositApprovalsDbName,
                DbPath = GetDbPathByNameConvention(ConsumerNdmDbConsts.ConsumerDepositApprovalsDbName),

                CacheIndexAndFilterBlocks = _ndmConfig.ConsumerDepositApprovalsDbCacheIndexAndFilterBlocks,
                BlockCacheSize = _ndmConfig.ConsumerDepositApprovalsDbBlockCacheSize,
                WriteBufferNumber = _ndmConfig.ConsumerDepositApprovalsDbWriteBufferNumber,
                WriteBufferSize = _ndmConfig.ConsumerDepositApprovalsDbWriteBufferSize,

                UpdateReadMetrics = () => ConsumerMetrics.ConsumerDepositApprovalsDbReads++,
                UpdateWriteMetrics = () => ConsumerMetrics.ConsumerDepositApprovalsDbWrites++,
            });
            RegisterDb(new RocksDbSettings()
            {
                DbName = ConsumerNdmDbConsts.ConsumerReceiptsDbName,
                DbPath = GetDbPathByNameConvention(ConsumerNdmDbConsts.ConsumerReceiptsDbName),

                CacheIndexAndFilterBlocks = _ndmConfig.ConsumerReceiptsDbCacheIndexAndFilterBlocks,
                BlockCacheSize = _ndmConfig.ConsumerReceiptsDbBlockCacheSize,
                WriteBufferNumber = _ndmConfig.ConsumerReceiptsDbWriteBufferNumber,
                WriteBufferSize = _ndmConfig.ConsumerReceiptsDbWriteBufferSize,

                UpdateReadMetrics = () => ConsumerMetrics.ConsumerReceiptsDbReads++,
                UpdateWriteMetrics = () => ConsumerMetrics.ConsumerReceiptsDbWrites++,
            });
            RegisterDb(new RocksDbSettings()
            {
                DbName = ConsumerNdmDbConsts.ConsumerSessionsDbName,
                DbPath = GetDbPathByNameConvention(ConsumerNdmDbConsts.ConsumerSessionsDbName),

                CacheIndexAndFilterBlocks = _ndmConfig.ConsumerSessionsDbCacheIndexAndFilterBlocks,
                BlockCacheSize = _ndmConfig.ConsumerSessionsDbBlockCacheSize,
                WriteBufferNumber = _ndmConfig.ConsumerSessionsDbWriteBufferNumber,
                WriteBufferSize = _ndmConfig.ConsumerSessionsDbWriteBufferSize,

                UpdateReadMetrics = () => ConsumerMetrics.ConsumerSessionsDbReads++,
                UpdateWriteMetrics = () => ConsumerMetrics.ConsumerSessionsDbWrites++,
            });
            RegisterDb(new RocksDbSettings()
            {
                DbName = ConsumerNdmDbConsts.DepositsDbName,
                DbPath = GetDbPathByNameConvention(ConsumerNdmDbConsts.DepositsDbName),

                CacheIndexAndFilterBlocks = _ndmConfig.DepositsDbCacheIndexAndFilterBlocks,
                BlockCacheSize = _ndmConfig.DepositsDbBlockCacheSize,
                WriteBufferNumber = _ndmConfig.DepositsDbWriteBufferNumber,
                WriteBufferSize = _ndmConfig.DepositsDbWriteBufferSize,

                UpdateReadMetrics = () => ConsumerMetrics.DepositsDbReads++,
                UpdateWriteMetrics = () => ConsumerMetrics.DepositsDbWrites++,
            });

            InitAll();
        }
    }
}
