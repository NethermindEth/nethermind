using Nethermind.Config;
namespace Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Rocks
{
    public interface IProviderDbConfig : IConfig
    {
        ulong ConsumersDbWriteBufferSize { get; set; }
        uint ConsumersDbWriteBufferNumber { get; set; }
        ulong ConsumersDbBlockCacheSize { get; set; }
        bool ConsumersDbCacheIndexAndFilterBlocks { get; set; }
        
        ulong DataAssetsDbWriteBufferSize { get; set; }
        uint DataAssetsDbWriteBufferNumber { get; set; }
        ulong DataAssetsDbBlockCacheSize { get; set; }
        bool DataAssetsDbCacheIndexAndFilterBlocks { get; set; }
        
        ulong PaymentClaimsDbWriteBufferSize { get; set; }
        uint PaymentClaimsDbWriteBufferNumber { get; set; }
        ulong PaymentClaimsDbBlockCacheSize { get; set; }
        bool PaymentClaimsDbCacheIndexAndFilterBlocks { get; set; }

        ulong ProviderSessionsDbWriteBufferSize { get; set; }
        uint ProviderSessionsDbWriteBufferNumber { get; set; }
        ulong ProviderSessionsDbBlockCacheSize { get; set; }
        bool ProviderSessionsDbCacheIndexAndFilterBlocks { get; set; }
        
        ulong ProviderReceiptsDbWriteBufferSize { get; set; }
        uint ProviderReceiptsDbWriteBufferNumber { get; set; }
        ulong ProviderReceiptsDbBlockCacheSize { get; set; }
        bool ProviderReceiptsDbCacheIndexAndFilterBlocks { get; set; }
        
        ulong ProviderDepositApprovalsDbWriteBufferSize { get; set; }
        uint ProviderDepositApprovalsDbWriteBufferNumber { get; set; }
        ulong ProviderDepositApprovalsDbBlockCacheSize { get; set; }
        bool ProviderDepositApprovalsDbCacheIndexAndFilterBlocks { get; set; }
    }
}