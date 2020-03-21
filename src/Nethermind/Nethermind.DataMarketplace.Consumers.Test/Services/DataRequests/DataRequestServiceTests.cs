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
using Nethermind.DataMarketplace.Consumers.DataRequests;
using Nethermind.DataMarketplace.Consumers.DataRequests.Services;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Consumers.Notifiers.Services;
using Nethermind.DataMarketplace.Consumers.Providers;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Consumers.Sessions.Queries;
using Nethermind.DataMarketplace.Consumers.Sessions.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Logging;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.DataRequests
{
    public class DataRequestServiceTests
    {
        private IDataRequestFactory _dataRequestFactory;
        private IDepositProvider _depositProvider;
        private IKycVerifier _kycVerifier;
        private IWallet _wallet;
        private IProviderService _providerService;
        private ITimestamper _timestamper;
        private IConsumerSessionRepository _sessionRepository;
        private IConsumerNotifier _consumerNotifier;
        private IDataRequestService _dataRequestService;
        private const uint DepositConfirmationTimestamp = 1546300800;
        private const uint DepositExpiryTime = 1546393600;
        private static readonly DateTime Date = new DateTime(2019, 1, 2); //1546383600
        private INdmNotifier _notifier;

        [SetUp]
        public void Setup()
        {
            _dataRequestFactory = Substitute.For<IDataRequestFactory>();
            _depositProvider = Substitute.For<IDepositProvider>();
            _kycVerifier = Substitute.For<IKycVerifier>();
            _wallet = Substitute.For<IWallet>();
            _providerService = Substitute.For<IProviderService>();
            _timestamper = new Timestamper(Date);
            _sessionRepository = Substitute.For<IConsumerSessionRepository>();
            _notifier = Substitute.For<INdmNotifier>();
            _consumerNotifier = new ConsumerNotifier(_notifier);
            _dataRequestService = new DataRequestService(_dataRequestFactory, _depositProvider, _kycVerifier, _wallet,
                _providerService, _timestamper, _sessionRepository, _consumerNotifier, LimboLogs.Instance);
        }

        [Test]
        public async Task send_data_request_should_fail_for_missing_deposit()
        {
            var depositId = Keccak.Zero;

            var result = await _dataRequestService.SendAsync(depositId);

            result.Should().Be(DataRequestResult.DepositNotFound);
            await _depositProvider.Received(1).GetAsync(depositId);
        }

        [Test]
        public async Task send_data_request_should_fail_for_locked_account()
        {
            var depositId = Keccak.Zero;
            var deposit = GetDepositDetails();
            _depositProvider.GetAsync(depositId).Returns(deposit);

            var result = await _dataRequestService.SendAsync(depositId);

            result.Should().Be(DataRequestResult.ConsumerAccountLocked);
            await _depositProvider.Received(1).GetAsync(depositId);
            _wallet.Received(1).IsUnlocked(deposit.Consumer);
        }

        [Test]
        public async Task send_data_request_should_fail_for_unverified_deposit_which_requires_kyc_verification()
        {
            var depositId = Keccak.Zero;
            var deposit = GetDepositDetails(kycRequired: true);
            _depositProvider.GetAsync(depositId).Returns(deposit);
            _wallet.IsUnlocked(deposit.Consumer).Returns(true);

            var result = await _dataRequestService.SendAsync(depositId);

            result.Should().Be(DataRequestResult.KycUnconfirmed);
            await _depositProvider.Received(1).GetAsync(depositId);
            _wallet.Received(1).IsUnlocked(deposit.Consumer);
            await _kycVerifier.Received(1).IsVerifiedAsync(deposit.DataAsset.Id, deposit.Consumer);
        }

        [Test]
        public async Task send_data_request_should_fail_for_unconfirmed_deposit()
        {
            var depositId = Keccak.Zero;
            var deposit = GetDepositDetails();
            _depositProvider.GetAsync(depositId).Returns(deposit);
            _wallet.IsUnlocked(deposit.Consumer).Returns(true);

            var result = await _dataRequestService.SendAsync(depositId);

            result.Should().Be(DataRequestResult.DepositUnconfirmed);
            await _depositProvider.Received(1).GetAsync(depositId);
            _wallet.Received(1).IsUnlocked(deposit.Consumer);
            await _kycVerifier.DidNotReceive().IsVerifiedAsync(deposit.DataAsset.Id, deposit.Consumer);
        }
        
        [Test]
        public async Task send_data_request_should_fail_for_expired_deposit()
        {
            var depositId = Keccak.Zero;
            var deposit = GetDepositDetails(DepositConfirmationTimestamp, DepositExpiryTime - 20000);
            _depositProvider.GetAsync(depositId).Returns(deposit);
            _wallet.IsUnlocked(deposit.Consumer).Returns(true);

            var result = await _dataRequestService.SendAsync(depositId);

            result.Should().Be(DataRequestResult.DepositExpired);
            await _depositProvider.Received(1).GetAsync(depositId);
            _wallet.Received(1).IsUnlocked(deposit.Consumer);
            await _kycVerifier.DidNotReceive().IsVerifiedAsync(deposit.DataAsset.Id, deposit.Consumer);
        }
        
        [Test]
        public async Task send_data_request_should_fail_for_missing_provider()
        {
            var depositId = Keccak.Zero;
            var deposit = GetDepositDetails(DepositConfirmationTimestamp, DepositExpiryTime);
            _depositProvider.GetAsync(depositId).Returns(deposit);
            _wallet.IsUnlocked(deposit.Consumer).Returns(true);
            _providerService.GetPeer(deposit.DataAsset.Provider.Address).Returns(_ => null);

            var result = await _dataRequestService.SendAsync(depositId);

            result.Should().Be(DataRequestResult.ProviderNotFound);
            await _depositProvider.Received(1).GetAsync(depositId);
            _wallet.Received(1).IsUnlocked(deposit.Consumer);
            await _kycVerifier.DidNotReceive().IsVerifiedAsync(deposit.DataAsset.Id, deposit.Consumer);
            _providerService.Received(1).GetPeer(deposit.DataAsset.Provider.Address);
        }

        [Test]
        public async Task send_data_request_should_succeed_for_valid_deposit()
        {
            var depositId = Keccak.Zero;
            var deposit = GetDepositDetails(DepositConfirmationTimestamp, DepositExpiryTime);
            _depositProvider.GetAsync(depositId).Returns(deposit);
            _wallet.IsUnlocked(deposit.Consumer).Returns(true);
            var provider = Substitute.For<INdmPeer>();
            _providerService.GetPeer(deposit.DataAsset.Provider.Address).Returns(provider);
            var sessions = new List<ConsumerSession>
            {
                GetConsumerSession()
            };
            var dataRequest = new DataRequest(Keccak.OfAnEmptyString, 1, 1, 1, Array.Empty<byte>(), Address.Zero,
                Address.Zero, null);
            var sessionsPagedResult = PagedResult<ConsumerSession>.Create(sessions, 1, 1, 1, 1);
            _sessionRepository.BrowseAsync(Arg.Any<GetConsumerSessions>()).Returns(sessionsPagedResult);
            _dataRequestFactory.Create(deposit.Deposit, deposit.DataAsset.Id, deposit.DataAsset.Provider.Address,
                deposit.Consumer, deposit.Pepper).Returns(dataRequest);
            var consumedUnits = (uint) sessions.Sum(s => s.ConsumedUnits);
            provider.SendDataRequestAsync(dataRequest, consumedUnits).Returns(DataRequestResult.DepositVerified);

            var result = await _dataRequestService.SendAsync(depositId);

            result.Should().Be(DataRequestResult.DepositVerified);
            await _depositProvider.Received(1).GetAsync(depositId);
            _wallet.Received(1).IsUnlocked(deposit.Consumer);
            await _kycVerifier.DidNotReceive().IsVerifiedAsync(deposit.DataAsset.Id, deposit.Consumer);
            _providerService.Received(1).GetPeer(deposit.DataAsset.Provider.Address);
            await _sessionRepository.Received(1).BrowseAsync(Arg.Any<GetConsumerSessions>());
            _dataRequestFactory.Received(1).Create(deposit.Deposit, deposit.DataAsset.Id,
                deposit.DataAsset.Provider.Address, deposit.Consumer, deposit.Pepper);
            await provider.Received(1).SendDataRequestAsync(dataRequest, consumedUnits);
            await _notifier.ReceivedWithAnyArgs(1).NotifyAsync(null);
        }

        private static ConsumerSession GetConsumerSession()
            => new ConsumerSession(Keccak.Zero, Keccak.Zero, Keccak.OfAnEmptyString, TestItem.AddressA,
                TestItem.PublicKeyA, TestItem.AddressB, TestItem.PublicKeyB, SessionState.Started,
                0, 0, dataAvailability: DataAvailability.Available);

        private static DepositDetails GetDepositDetails(uint confirmationTimestamp = 0, uint expiryTime = 1,
            bool kycRequired = false)
            => new DepositDetails(new Deposit(Keccak.Zero, 1, expiryTime, 1),
                GetDataAsset(kycRequired), TestItem.AddressB, Array.Empty<byte>(), 1,
                new []{TransactionInfo.Default(TestItem.KeccakA, 1, 1, 1, 1)}, confirmationTimestamp);

        private static DataAsset GetDataAsset(bool kycRequired = false)
            => new DataAsset(Keccak.OfAnEmptyString, "test", "test", 1,
                DataAssetUnitType.Unit, 0, 10, new DataAssetRules(new DataAssetRule(1)),
                new DataAssetProvider(Address.Zero, "test"), kycRequired: kycRequired);
    }
}