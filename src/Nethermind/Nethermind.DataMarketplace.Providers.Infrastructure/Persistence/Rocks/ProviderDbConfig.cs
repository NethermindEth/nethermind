using Nethermind.Core.Extensions;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Rocks
{
    public class ProviderDbConfig : IProviderDbConfig
    {
        public ulong ConsumersDbWriteBufferSize { get; set; } = (ulong)16.MB();
        public uint ConsumersDbWriteBufferNumber { get; set; } = 4;
        public ulong ConsumersDbBlockCacheSize { get; set; } = (ulong)64.MB();
        public bool ConsumersDbCacheIndexAndFilterBlocks { get; set; } = true;

        public ulong DataAssetsDbWriteBufferSize { get; set; } = (ulong)16.MB();
        public uint DataAssetsDbWriteBufferNumber { get; set; } = 4;
        public ulong DataAssetsDbBlockCacheSize { get; set; } = (ulong)64.MB();
        public bool DataAssetsDbCacheIndexAndFilterBlocks { get; set; } = true;

        public ulong PaymentClaimsDbWriteBufferSize { get; set; } = (ulong)16.MB();
        public uint PaymentClaimsDbWriteBufferNumber { get; set; } = 4;
        public ulong PaymentClaimsDbBlockCacheSize { get; set; } = (ulong)64.MB();
        public bool PaymentClaimsDbCacheIndexAndFilterBlocks { get; set; } = true;

        public ulong ProviderSessionsDbWriteBufferSize { get; set; } = (ulong)16.MB();
        public uint ProviderSessionsDbWriteBufferNumber { get; set; } = 4;
        public ulong ProviderSessionsDbBlockCacheSize { get; set; } = (ulong)64.MB();
        public bool ProviderSessionsDbCacheIndexAndFilterBlocks { get; set; } = true;

        public ulong ProviderReceiptsDbWriteBufferSize { get; set; } = (ulong)16.MB();
        public uint ProviderReceiptsDbWriteBufferNumber { get; set; } = 4;
        public ulong ProviderReceiptsDbBlockCacheSize { get; set; } = (ulong)64.MB();
        public bool ProviderReceiptsDbCacheIndexAndFilterBlocks { get; set; } = true;

        public ulong ProviderDepositApprovalsDbWriteBufferSize { get; set; } = (ulong)16.MB();
        public uint ProviderDepositApprovalsDbWriteBufferNumber { get; set; } = 4;
        public ulong ProviderDepositApprovalsDbBlockCacheSize { get; set; } = (ulong)64.MB();
        public bool ProviderDepositApprovalsDbCacheIndexAndFilterBlocks { get; set; } = true;
    }
}