// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Providers.Validators
{
    internal interface IDataAvailabilityValidator
    {
        DataAvailability GetAvailability(DataAssetUnitType unitType, uint purchasedUnits,
            long consumedUnits, uint verificationTimestamp, ulong nowSeconds);
    }
}
