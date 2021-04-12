using Nethermind.Db;
using System;
using System.Threading.Tasks;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Rocks
{
    public static class ProviderDbNames
    {
        public const string Consumers = "consumers";
        public const string DataAssets = "dataAssets";
        public const string PaymentClaims = "paymentClaims";
        public const string ProviderDepositApprovals = "providerDepositApprovals";
        public const string ProviderReceipts = "providerReceipts";
        public const string ProviderSessions = "providerSessions";
    }

    public class ProviderDbInitializer : RocksDbInitializer
    {
        private readonly IProviderDbConfig _providerConfig;
        public ProviderDbInitializer(
            IDbProvider dbProvider,
            IProviderDbConfig providerConfig,
            IRocksDbFactory rocksDbFactory,
            IMemDbFactory memDbFactory)
            : base(dbProvider, rocksDbFactory, memDbFactory)
        {
            _providerConfig = providerConfig ?? throw new ArgumentNullException(nameof(providerConfig));
        }
        public async Task Init()
        {
            RegisterDb(new RocksDbSettings(GetTitleDbName(ProviderDbNames.Consumers), ProviderDbNames.Consumers)
            {
                CacheIndexAndFilterBlocks = _providerConfig.ConsumersDbCacheIndexAndFilterBlocks,
                BlockCacheSize = _providerConfig.ConsumersDbBlockCacheSize,
                WriteBufferNumber = _providerConfig.ConsumersDbWriteBufferNumber,
                WriteBufferSize = _providerConfig.ConsumersDbWriteBufferSize,

                UpdateReadMetrics = () => ProviderMetrics.ConsumersDbReads++,
                UpdateWriteMetrics = () => ProviderMetrics.ConsumersDbWrites++,
            });
            RegisterDb(new RocksDbSettings(GetTitleDbName(ProviderDbNames.DataAssets), ProviderDbNames.DataAssets)
            {
                CacheIndexAndFilterBlocks = _providerConfig.DataAssetsDbCacheIndexAndFilterBlocks,
                BlockCacheSize = _providerConfig.DataAssetsDbBlockCacheSize,
                WriteBufferNumber = _providerConfig.DataAssetsDbWriteBufferNumber,
                WriteBufferSize = _providerConfig.DataAssetsDbWriteBufferSize,

                UpdateReadMetrics = () => ProviderMetrics.DataAssetsDbReads++,
                UpdateWriteMetrics = () => ProviderMetrics.DataAssetsDbWrites++,
            });
            RegisterDb(new RocksDbSettings(GetTitleDbName(ProviderDbNames.PaymentClaims), ProviderDbNames.PaymentClaims)
            {
                CacheIndexAndFilterBlocks = _providerConfig.PaymentClaimsDbCacheIndexAndFilterBlocks,
                BlockCacheSize = _providerConfig.PaymentClaimsDbBlockCacheSize,
                WriteBufferNumber = _providerConfig.PaymentClaimsDbWriteBufferNumber,
                WriteBufferSize = _providerConfig.PaymentClaimsDbWriteBufferSize,

                UpdateReadMetrics = () => ProviderMetrics.PaymentClaimsDbReads++,
                UpdateWriteMetrics = () => ProviderMetrics.PaymentClaimsDbWrites++,
            });
            RegisterDb(new RocksDbSettings(GetTitleDbName(ProviderDbNames.ProviderDepositApprovals), ProviderDbNames.ProviderDepositApprovals)
            {
                CacheIndexAndFilterBlocks = _providerConfig.ProviderDepositApprovalsDbCacheIndexAndFilterBlocks,
                BlockCacheSize = _providerConfig.ProviderDepositApprovalsDbBlockCacheSize,
                WriteBufferNumber = _providerConfig.ProviderDepositApprovalsDbWriteBufferNumber,
                WriteBufferSize = _providerConfig.ProviderDepositApprovalsDbWriteBufferSize,

                UpdateReadMetrics = () => ProviderMetrics.ProviderDepositApprovalsDbReads++,
                UpdateWriteMetrics = () => ProviderMetrics.ProviderDepositApprovalsDbWrites++,
            });
            RegisterDb(new RocksDbSettings(GetTitleDbName(ProviderDbNames.ProviderReceipts), ProviderDbNames.ProviderReceipts)
            {
                CacheIndexAndFilterBlocks = _providerConfig.ProviderReceiptsDbCacheIndexAndFilterBlocks,
                BlockCacheSize = _providerConfig.ProviderReceiptsDbBlockCacheSize,
                WriteBufferNumber = _providerConfig.ProviderReceiptsDbWriteBufferNumber,
                WriteBufferSize = _providerConfig.ProviderReceiptsDbWriteBufferSize,

                UpdateReadMetrics = () => ProviderMetrics.ProviderReceiptsDbReads++,
                UpdateWriteMetrics = () => ProviderMetrics.ProviderReceiptsDbWrites++,
            });
            RegisterDb(new RocksDbSettings(GetTitleDbName(ProviderDbNames.ProviderSessions), ProviderDbNames.ProviderSessions)
            {
                CacheIndexAndFilterBlocks = _providerConfig.ProviderSessionsDbCacheIndexAndFilterBlocks,
                BlockCacheSize = _providerConfig.ProviderSessionsDbBlockCacheSize,
                WriteBufferNumber = _providerConfig.ProviderSessionsDbWriteBufferNumber,
                WriteBufferSize = _providerConfig.ProviderSessionsDbWriteBufferSize,

                UpdateReadMetrics = () => ProviderMetrics.ProviderSessionsDbReads++,
                UpdateWriteMetrics = () => ProviderMetrics.ProviderSessionsDbWrites++,
            });

            await InitAllAsync();
        }
    }
}
