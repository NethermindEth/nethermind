// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.Db;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks
{
    public class ConsumerNdmDbNames
    {
        public const string ConsumerDepositApprovals = "consumerDepositApprovals";
        public const string ConsumerReceipts = "consumerReceipts";
        public const string ConsumerSessions = "consumerSessions";
        public const string Deposits = "deposits";
    }

    public class ConsumerNdmDbInitializer : RocksDbInitializer
    {
        private readonly INdmConfig _ndmConfig;
        private static int _initialized;
        public ConsumerNdmDbInitializer(
            IDbProvider dbProvider,
            INdmConfig ndmConfig,
            IRocksDbFactory rocksDbFactory,
            IMemDbFactory memDbFactory)
            : base(dbProvider, rocksDbFactory, memDbFactory)
        {
            _ndmConfig = ndmConfig ?? throw new ArgumentNullException(nameof(NdmConfig));
        }

        public async Task InitAsync()
        {
            if (Interlocked.CompareExchange(ref _initialized, 1, 0) == 0)
            {
                RegisterDb(
                    new RocksDbSettings(GetTitleDbName(ConsumerNdmDbNames.ConsumerDepositApprovals), ConsumerNdmDbNames.ConsumerDepositApprovals)
                    {
                        CacheIndexAndFilterBlocks = _ndmConfig.ConsumerDepositApprovalsDbCacheIndexAndFilterBlocks,
                        BlockCacheSize = _ndmConfig.ConsumerDepositApprovalsDbBlockCacheSize,
                        WriteBufferNumber = _ndmConfig.ConsumerDepositApprovalsDbWriteBufferNumber,
                        WriteBufferSize = _ndmConfig.ConsumerDepositApprovalsDbWriteBufferSize,

                        UpdateReadMetrics = () => ConsumerMetrics.ConsumerDepositApprovalsDbReads++,
                        UpdateWriteMetrics = () => ConsumerMetrics.ConsumerDepositApprovalsDbWrites++,
                    });
                RegisterDb(new RocksDbSettings(GetTitleDbName(ConsumerNdmDbNames.ConsumerReceipts), ConsumerNdmDbNames.ConsumerReceipts)
                {
                    CacheIndexAndFilterBlocks = _ndmConfig.ConsumerReceiptsDbCacheIndexAndFilterBlocks,
                    BlockCacheSize = _ndmConfig.ConsumerReceiptsDbBlockCacheSize,
                    WriteBufferNumber = _ndmConfig.ConsumerReceiptsDbWriteBufferNumber,
                    WriteBufferSize = _ndmConfig.ConsumerReceiptsDbWriteBufferSize,

                    UpdateReadMetrics = () => ConsumerMetrics.ConsumerReceiptsDbReads++,
                    UpdateWriteMetrics = () => ConsumerMetrics.ConsumerReceiptsDbWrites++,
                });
                RegisterDb(new RocksDbSettings(GetTitleDbName(ConsumerNdmDbNames.ConsumerSessions), ConsumerNdmDbNames.ConsumerSessions)
                {
                    CacheIndexAndFilterBlocks = _ndmConfig.ConsumerSessionsDbCacheIndexAndFilterBlocks,
                    BlockCacheSize = _ndmConfig.ConsumerSessionsDbBlockCacheSize,
                    WriteBufferNumber = _ndmConfig.ConsumerSessionsDbWriteBufferNumber,
                    WriteBufferSize = _ndmConfig.ConsumerSessionsDbWriteBufferSize,

                    UpdateReadMetrics = () => ConsumerMetrics.ConsumerSessionsDbReads++,
                    UpdateWriteMetrics = () => ConsumerMetrics.ConsumerSessionsDbWrites++,
                });
                RegisterDb(new RocksDbSettings(GetTitleDbName(ConsumerNdmDbNames.Deposits), ConsumerNdmDbNames.Deposits)
                {
                    CacheIndexAndFilterBlocks = _ndmConfig.DepositsDbCacheIndexAndFilterBlocks,
                    BlockCacheSize = _ndmConfig.DepositsDbBlockCacheSize,
                    WriteBufferNumber = _ndmConfig.DepositsDbWriteBufferNumber,
                    WriteBufferSize = _ndmConfig.DepositsDbWriteBufferSize,

                    UpdateReadMetrics = () => ConsumerMetrics.DepositsDbReads++,
                    UpdateWriteMetrics = () => ConsumerMetrics.DepositsDbWrites++,
                });

                await InitAllAsync();
            }
        }

        public void Reset()
        {
            _initialized = 0;
        }
    }
}
