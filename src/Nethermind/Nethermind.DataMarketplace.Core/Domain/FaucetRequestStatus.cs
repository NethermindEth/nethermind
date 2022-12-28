// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
