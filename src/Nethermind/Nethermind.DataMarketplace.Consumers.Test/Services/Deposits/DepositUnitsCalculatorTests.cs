//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Services;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Consumers.Sessions.Queries;
using Nethermind.DataMarketplace.Consumers.Sessions.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Deposits
{
    public class DepositUnitsCalculatorTests
    {
        private IConsumerSessionRepository _sessionRepository;
        private ITimestamper _timestamper;
        private IDepositUnitsCalculator _calculator;
        private const uint DepositConfirmationTimestamp = 1546300800;
        private static readonly DateTime Date = new DateTime(2019, 1, 2); //1546383600

        [SetUp]
        public void Setup()
        {
            _sessionRepository = Substitute.For<IConsumerSessionRepository>();
            _timestamper = new Timestamper(Date);
            _calculator = new DepositUnitsCalculator(_sessionRepository, _timestamper);
        }

        [Test]
        public async Task should_return_0_consumed_units_for_unconfirmed_deposit()
        {
            var deposit = GetDepositDetails(confirmationTimestamp: 0);
            var consumedUnits = await _calculator.GetConsumedAsync(deposit);

            consumedUnits.Should().Be(0);

            await _sessionRepository.DidNotReceive().BrowseAsync(Arg.Any<GetConsumerSessions>());
        }

        [Test]
        public async Task should_return_0_consumed_units_for_confirmed_deposit_without_any_sessions_and_unit_unit_type()
        {
            var deposit = GetDepositDetails();
            _sessionRepository.BrowseAsync(Arg.Any<GetConsumerSessions>()).Returns(PagedResult<ConsumerSession>.Empty);

            var consumedUnits = await _calculator.GetConsumedAsync(deposit);

            consumedUnits.Should().Be(0);
            await _sessionRepository.Received(1).BrowseAsync(Arg.Any<GetConsumerSessions>());
        }

        [Test]
        public async Task should_return_sum_of_consumed_units_for_confirmed_deposit_with_sessions_and_unit_unit_type()
        {
            var deposit = GetDepositDetails();
            var sessions = new List<ConsumerSession>
            {
                GetConsumerSession(10),
                GetConsumerSession(20),
                GetConsumerSession(30)
            };
            var expectedConsumedUnits = (uint) sessions.Sum(s => s.ConsumedUnits);

            _sessionRepository.BrowseAsync(Arg.Any<GetConsumerSessions>())
                .Returns(PagedResult<ConsumerSession>.Create(sessions, 1, 1, 1, sessions.Count));

            var consumedUnits = await _calculator.GetConsumedAsync(deposit);

            consumedUnits.Should().Be(expectedConsumedUnits);
            await _sessionRepository.Received(1).BrowseAsync(Arg.Any<GetConsumerSessions>());
        }

        [Test]
        public async Task should_return_consumed_units_based_on_timestamp_for_confirmed_deposit_with_time_unit_type()
        {
            var deposit = GetDepositDetails(DataAssetUnitType.Time);
            var expectedConsumedUnits = (uint) _timestamper.UnixTime.Seconds - DepositConfirmationTimestamp;

            var consumedUnits = await _calculator.GetConsumedAsync(deposit);

            consumedUnits.Should().Be(expectedConsumedUnits);
            await _sessionRepository.DidNotReceive().BrowseAsync(Arg.Any<GetConsumerSessions>());
        }

        private static ConsumerSession GetConsumerSession(uint consumedUnits = 0)
            => new ConsumerSession(Keccak.Zero, Keccak.Zero, Keccak.OfAnEmptyString, TestItem.AddressA,
                TestItem.PublicKeyA, TestItem.AddressB, TestItem.PublicKeyB, SessionState.Started,
                0, 0, dataAvailability: DataAvailability.Available, consumedUnits: consumedUnits);

        private static DepositDetails GetDepositDetails(DataAssetUnitType unitType = DataAssetUnitType.Unit,
            uint confirmationTimestamp = DepositConfirmationTimestamp)
            => new DepositDetails(new Deposit(Keccak.Zero, 1, 1, 1),
                GetDataAsset(unitType), TestItem.AddressB, Array.Empty<byte>(), 1,
                new []{TransactionInfo.Default(TestItem.KeccakA, 1, 1, 1,1)}, confirmationTimestamp);

        private static DataAsset GetDataAsset(DataAssetUnitType unitType)
            => new DataAsset(Keccak.OfAnEmptyString, "test", "test", 1,
                unitType, 0, 10, new DataAssetRules(new DataAssetRule(1)),
                new DataAssetProvider(Address.Zero, "test"));
    }
}
