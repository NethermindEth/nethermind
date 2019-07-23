namespace Nethermind.DataMarketplace.Core.Domain
{
    public enum InvalidDataReason
    {
        DataAssetNotFound,
        DepositNotFound,
        SessionNotFound,
        NoUnitsLeft,
        PluginNotFound,
        PluginDisabled,
        InvalidResult,
        InternalError
    }
}