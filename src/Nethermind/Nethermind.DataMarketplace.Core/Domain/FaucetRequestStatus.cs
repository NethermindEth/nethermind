namespace Nethermind.DataMarketplace.Core.Domain
{
    public enum FaucetRequestStatus
    {
        FaucetNotSet,
        FaucetDisabled,
        FaucetAddressNotSet,
        InvalidNodeAddress,
        SameAddressAsFaucet,
        ZeroValue,
        TooBigValue,
        DailyRequestsTotalValueReached,
        ProcessingRequestError,
        RequestAlreadyProcessing,
        RequestAlreadyProcessedToday,
        RequestError,
        RequestCompleted,
    }
}