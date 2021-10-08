namespace Nethermind.DataMarketplace.Providers
{
    public static class Metrics
    {
        public static long ProviderReceivedQueries { get; set; }
        public static long ProviderSuccessfulQueries { get; set; }
        public static long ProviderInvalidQueries { get; set; }
        public static long ProviderFailedQueries { get; set; }
    }
}