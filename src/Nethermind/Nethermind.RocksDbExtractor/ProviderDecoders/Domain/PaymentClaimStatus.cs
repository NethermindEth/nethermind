namespace Nethermind.RocksDbExtractor.ProviderDecoders.Domain
{
    public enum PaymentClaimStatus
    {
        Unknown,
        Sent,
        Claimed,
        ClaimedWithLoss,
        Rejected,
        Cancelled
    }
}
