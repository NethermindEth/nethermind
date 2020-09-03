namespace Nethermind.RocksDbExtractor.Domain
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
