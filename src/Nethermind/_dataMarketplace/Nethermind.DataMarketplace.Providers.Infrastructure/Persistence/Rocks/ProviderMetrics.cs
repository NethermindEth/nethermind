namespace Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Rocks
{
    internal static class ProviderMetrics
    {
        public static long ConsumersDbReads { get; set; }
        public static long ConsumersDbWrites { get; set; }
        public static long DataAssetsDbReads { get; set; }
        public static long DataAssetsDbWrites { get; set; }
        public static long PaymentClaimsDbReads { get; set; }
        public static long PaymentClaimsDbWrites { get; set; }
        public static long ProviderDepositApprovalsDbReads { get; set; }
        public static long ProviderDepositApprovalsDbWrites { get; set; }
        public static long ProviderReceiptsDbReads { get; set; }
        public static long ProviderReceiptsDbWrites { get; set; }
        public static long ProviderSessionsDbReads { get; set; }
        public static long ProviderSessionsDbWrites { get; set; }
    }
}