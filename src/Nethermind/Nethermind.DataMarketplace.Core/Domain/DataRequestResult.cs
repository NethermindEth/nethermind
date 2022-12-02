// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.DataMarketplace.Core.Domain
{
    public enum DataRequestResult
    {
        ConsumerAccountLocked,
        ProviderUnavailable,
        DepositNotFound,
        DepositUnconfirmed,
        DepositUnverified,
        DepositExpired,
        KycUnconfirmed,
        ProviderNotFound,
        InvalidSignature,
        DataAssetNotFound,
        DataAssetClosed,
        InvalidDataRequestUnits,
        InvalidDataRequestValue,
        DepositVerified
    }
}
