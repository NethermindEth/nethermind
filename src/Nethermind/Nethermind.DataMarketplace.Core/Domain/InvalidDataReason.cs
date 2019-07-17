namespace Nethermind.DataMarketplace.Core.Domain
{
    public enum InvalidDataReason
    {
        DataHeaderNotFound,
        DepositNotFound,
        SessionNotFound,
        NoUnitsLeft,
        PluginNotFound,
        PluginDisabled,
        InvalidResult,
        InternalError
    }
}