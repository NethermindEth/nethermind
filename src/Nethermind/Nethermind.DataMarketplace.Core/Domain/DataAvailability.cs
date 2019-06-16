namespace Nethermind.DataMarketplace.Core.Domain
{
    public enum DataAvailability
    {
        Unknown,
        Available,
        SubscriptionEnded,
        ExpiryRuleExceeded,
        UnitsExceeded,
        DataDeliveryReceiptNotProvided,
        DataDeliveryReceiptInvalid
    }
}