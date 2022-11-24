// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Providers.Validators
{
    public class DataAvailabilityValidator : IDataAvailabilityValidator
    {
        public DataAvailability GetAvailability(DataAssetUnitType unitType, uint purchasedUnits,
            long consumedUnits, uint verificationTimestamp, ulong nowSeconds)
        {
            switch (unitType)
            {
                case DataAssetUnitType.Time:
                    if (verificationTimestamp + purchasedUnits > nowSeconds)
                    {
                        return DataAvailability.Available;
                    }

                    return DataAvailability.SubscriptionEnded;
                case DataAssetUnitType.Unit:
                    if (consumedUnits < purchasedUnits)
                    {
                        return DataAvailability.Available;
                    }

                    return DataAvailability.UnitsExceeded;

                default: return DataAvailability.Available;
            }
        }
    }
}
