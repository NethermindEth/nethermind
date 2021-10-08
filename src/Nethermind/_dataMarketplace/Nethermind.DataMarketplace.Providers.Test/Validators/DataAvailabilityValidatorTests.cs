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

using FluentAssertions;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Validators;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Providers.Test.Validators
{
    internal class DataAvailabilityValidatorTests
    {
        private IDataAvailabilityValidator _validator;

        [SetUp]
        public void Setup()
        {
            _validator = new DataAvailabilityValidator();
        }

        [Test]
        public void given_time_unit_type_and_start_timestamp_and_request_units_in_range_data_should_be_available()
            => TestTime(10, 10, 19, DataAvailability.Available);

        [Test]
        public void given_time_unit_type_and_start_timestamp_and_request_units_not_in_range_data_should_be_unavailable()
            => TestTime(10, 0, 10, DataAvailability.SubscriptionEnded);

        [Test]
        public void given_unit_unit_type_and_consumer_total_units_lower_than_request_units_data_should_be_available()
            => TestUnits(11, 10, DataAvailability.Available);

        [Test]
        public void given_unit_unit_type_and_consumer_total_units_equal_request_units_data_should_be_unavailable()
            => TestUnits(10, 10, DataAvailability.UnitsExceeded);

        private void TestUnits(uint depositedUnits, long consumedUnits, DataAvailability availability)
            => Test(DataAssetUnitType.Unit, depositedUnits, consumedUnits, 0, 0, availability);

        private void TestTime(uint depositedUnits, uint startTimestamp, ulong nowSeconds, DataAvailability availability)
            => Test(DataAssetUnitType.Time, depositedUnits, 0, startTimestamp, nowSeconds, availability);

        private void Test(DataAssetUnitType unitType, uint depositedUnits,
            long consumedUnits, uint startTimestamp, ulong nowSeconds, DataAvailability availability)
        {
            var dataAvailability = _validator.GetAvailability(unitType, depositedUnits, consumedUnits,
                startTimestamp, nowSeconds);
            dataAvailability.Should().Be(availability);
        }
    }
}