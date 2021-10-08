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
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.DataStreams;
using Nethermind.DataMarketplace.Consumers.DataStreams.Services;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Consumers.Notifiers.Services;
using Nethermind.DataMarketplace.Consumers.Sessions;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Consumers.Sessions.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.DataStreams
{
    public class DataConsumerServiceTests
    {
        private IDepositProvider _depositProvider;
        private ISessionService _sessionService;
        private IConsumerNotifier _consumerNotifier;
        private ITimestamper _timestamper;
        private IConsumerSessionRepository _sessionRepository;
        private IDataConsumerService _dataConsumerService;
        private const uint DepositConfirmationTimestamp = 1546300800;
        private const ulong SessionStartTimestamp = 1546320800;
        private static readonly DateTime Date = new DateTime(2019, 1, 2); //1546383600

        [SetUp]
        public void Setup()
        {
            _depositProvider = Substitute.For<IDepositProvider>();
            _sessionService = Substitute.For<ISessionService>();
            _consumerNotifier = Substitute.For<IConsumerNotifier>();
            _timestamper = new Timestamper(Date);
            _sessionRepository = Substitute.For<IConsumerSessionRepository>();
            _dataConsumerService = new DataConsumerService(_depositProvider, _sessionService, _consumerNotifier,
                _timestamper, _sessionRepository, LimboLogs.Instance);
        }
        [Test]
        public async Task set_units_should_fail_for_missing_session()
        {
            var depositId = Keccak.Zero;
            var consumedUnitsFromProvider = 0u;

            await _dataConsumerService.SetUnitsAsync(depositId, consumedUnitsFromProvider);

            _sessionService.Received(1).GetActive(depositId);
            await _depositProvider.DidNotReceive().GetAsync(depositId);
            await _sessionRepository.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<ConsumerSession>());
        }

        [Test]
        public async Task set_units_should_fail_for_missing_deposit()
        {
            var depositId = Keccak.Zero;
            var consumedUnitsFromProvider = 0u;
            var session = GetConsumerSession();
            _sessionService.GetActive(depositId).Returns(session);

            await _dataConsumerService.SetUnitsAsync(depositId, consumedUnitsFromProvider);

            _sessionService.Received(1).GetActive(depositId);
            await _depositProvider.Received(1).GetAsync(depositId);
            await _sessionRepository.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<ConsumerSession>());
        }

        [Test]
        public async Task set_units_should_update_units_and_session_for_unit_unit_type()
            => await VerifyUnitsAsync(DataAssetUnitType.Unit, 10,
                (d, s) => 1,
                (d, s) => 1);

        [Test]
        public async Task set_units_should_update_units_and_session_for_time_unit_type()
            => await VerifyUnitsAsync(DataAssetUnitType.Time, 10,
                (d, s) => (uint) (_timestamper.UnixTime.Seconds - s.StartTimestamp),
                (d, s) => (uint) (_timestamper.UnixTime.Seconds - d.ConfirmationTimestamp - s.PaidUnits));

        private async Task VerifyUnitsAsync(DataAssetUnitType unitType, uint paidUnits,
            Func<DepositDetails, Session, uint> expectedConsumedUnits,
            Func<DepositDetails, Session, uint> expectedUnpaidUnits)
        {
            var depositId = Keccak.Zero;
            var consumedUnitsFromProvider = 10u;
            var session = GetConsumerSession(paidUnits);
            var deposit = GetDepositDetails(unitType);
            _sessionService.GetActive(depositId).Returns(session);
            _depositProvider.GetAsync(session.DepositId).Returns(deposit);

            await _dataConsumerService.SetUnitsAsync(depositId, consumedUnitsFromProvider);

            session.ConsumedUnitsFromProvider.Should().Be(consumedUnitsFromProvider);
            session.ConsumedUnits.Should().Be(expectedConsumedUnits(deposit, session));
            session.UnpaidUnits.Should().Be(expectedUnpaidUnits(deposit, session));
            await _depositProvider.Received(1).GetAsync(depositId);
            _sessionService.Received(1).GetActive(depositId);
            await _sessionRepository.Received(1).UpdateAsync(session);
        }

        [Test]
        public async Task data_availability_should_not_be_updated_for_missing_session()
        {
            var depositId = Keccak.Zero;
            var dataAvailability = DataAvailability.Available;

            await _dataConsumerService.SetDataAvailabilityAsync(depositId, dataAvailability);
            
            _sessionService.Received(1).GetActive(depositId);
            await _sessionRepository.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<ConsumerSession>());
            await _consumerNotifier.DidNotReceiveWithAnyArgs()
                .SendDataAvailabilityChangedAsync(depositId, Arg.Any<Keccak>(), dataAvailability);
        }

        [Test]
        public async Task data_availability_should_be_updated_when_session_exists()
        {
            var depositId = Keccak.Zero;
            var dataAvailability = DataAvailability.Available;
            var session = GetConsumerSession();
            _sessionService.GetActive(depositId).Returns(session);

            await _dataConsumerService.SetDataAvailabilityAsync(depositId, dataAvailability);

            session.DataAvailability.Should().Be(dataAvailability);
            _sessionService.Received(1).GetActive(depositId);
            await _sessionRepository.Received(1).UpdateAsync(session);
            await _consumerNotifier.Received(1)
                .SendDataAvailabilityChangedAsync(depositId, session.Id, dataAvailability);
        }
        
        [Test]
        public async Task handling_invalid_data_should_fail_for_missing_session()
        {
            var depositId = Keccak.Zero;
            var reason = InvalidDataReason.InvalidResult;
            
            await _dataConsumerService.HandleInvalidDataAsync(depositId, reason);
            
            _sessionService.Received(1).GetActive(depositId);
            await _consumerNotifier.DidNotReceive().SendDataInvalidAsync(depositId, reason);
        }

        [Test]
        public async Task handling_invalid_data_should_result_in_sending_notification_to_consumer()
        {
            var depositId = Keccak.Zero;
            var reason = InvalidDataReason.InvalidResult;
            var session = GetConsumerSession();
            _sessionService.GetActive(depositId).Returns(session);
            
            await _dataConsumerService.HandleInvalidDataAsync(depositId, reason);
            
            _sessionService.Received(1).GetActive(depositId);
            await _consumerNotifier.Received(1).SendDataInvalidAsync(depositId, reason);
        }
        
        [Test]
        public async Task handling_exceeded_grace_units_should_fail_for_missing_session()
        {
            var depositId = Keccak.Zero;
            var consumedUnitsFromProvider = 10u;
            var graceUnits = 5u;

            await _dataConsumerService.HandleGraceUnitsExceededAsync(depositId, consumedUnitsFromProvider, graceUnits);

            _sessionService.Received(1).GetActive(depositId);
            await _consumerNotifier.DidNotReceiveWithAnyArgs().SendGraceUnitsExceeded(depositId, Arg.Any<string>(),
                consumedUnitsFromProvider, Arg.Any<uint>(), graceUnits);
        }

        [Test]
        public async Task handling_exceeded_grace_units_should_result_in_sending_notification_to_consumer()
        {
            var depositId = Keccak.Zero;
            var consumedUnitsFromProvider = 10u;
            var graceUnits = 5u;
            var session = GetConsumerSession();
            var deposit = GetDepositDetails();
            _sessionService.GetActive(depositId).Returns(session);
            _depositProvider.GetAsync(session.DepositId).Returns(deposit);

            await _dataConsumerService.HandleGraceUnitsExceededAsync(depositId, consumedUnitsFromProvider, graceUnits);

            _sessionService.Received(1).GetActive(depositId);
            await _consumerNotifier.Received(1).SendGraceUnitsExceeded(depositId, deposit.DataAsset.Name,
                consumedUnitsFromProvider, session.ConsumedUnits, graceUnits);
        }

        private static ConsumerSession GetConsumerSession(uint paidUnits = 0)
            => new ConsumerSession(Keccak.Zero, Keccak.Zero, Keccak.OfAnEmptyString, TestItem.AddressA,
                TestItem.PublicKeyA, TestItem.AddressB, TestItem.PublicKeyB, SessionState.Started,
                0, 0, dataAvailability: DataAvailability.Available, startTimestamp: SessionStartTimestamp,
                paidUnits: paidUnits);

        private static DepositDetails GetDepositDetails(DataAssetUnitType unitType = DataAssetUnitType.Unit)
            => new DepositDetails(new Deposit(Keccak.Zero, 1, 1, 1),
                GetDataAsset(unitType), TestItem.AddressB, Array.Empty<byte>(), 1,
                new []{TransactionInfo.Default(TestItem.KeccakA, 1, 1, 1, 1)}, DepositConfirmationTimestamp);

        private static DataAsset GetDataAsset(DataAssetUnitType unitType)
            => new DataAsset(Keccak.OfAnEmptyString, "test", "test", 1,
                unitType, 0, 10, new DataAssetRules(new DataAssetRule(1)),
                new DataAssetProvider(Address.Zero, "test"));
    }
}
