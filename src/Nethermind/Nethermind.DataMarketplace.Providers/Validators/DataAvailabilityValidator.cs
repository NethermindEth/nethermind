/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

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