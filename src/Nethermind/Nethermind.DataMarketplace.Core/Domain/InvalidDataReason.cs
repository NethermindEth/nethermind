namespace Nethermind.DataMarketplace.Core.Domain
{
    public enum InvalidDataReason
    {
        DataAssetNotFound,
        DepositNotFound,
        SessionNotFound,
        SessionClientNotFound,
        NoUnitsLeft,
        PluginNotFound,
        PluginDisabled,
        InvalidResult,
        InternalError
    }
}