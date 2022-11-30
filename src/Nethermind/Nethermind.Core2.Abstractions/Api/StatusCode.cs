// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core2.Api
{
    public enum StatusCode
    {
        Success = 200,
        BroadcastButFailedValidation = 202,
        InvalidRequest = 400,
        ValidatorNotFound = 404,
        DutiesNotAvailableForRequestedEpoch = 406,
        InternalError = 500,
        CurrentlySyncing = 503
    }
}
