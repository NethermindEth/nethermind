namespace Nethermind.DataMarketplace.Core.Domain
{
    public enum InvalidDataReason
    {
        DataHeaderNotFound,
        DepositNotFound,
        NoUnitsLeft,
        PluginNotFound,
        PluginDisabled,
        InvalidResult
    }
}