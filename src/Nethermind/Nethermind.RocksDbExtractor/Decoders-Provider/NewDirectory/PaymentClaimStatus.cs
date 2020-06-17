namespace Nethermind.DataMarketplace.Providers.Domain
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