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
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.DataAssets;
using Nethermind.DataMarketplace.Consumers.DataStreams;
using Nethermind.DataMarketplace.Consumers.DataStreams.Services;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Consumers.Notifiers.Services;
using Nethermind.DataMarketplace.Consumers.Providers;
using Nethermind.DataMarketplace.Consumers.Sessions;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Consumers.Sessions.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Logging;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.DataStreams
{
    public class DataStreamServiceTests
    {
        private IDataAssetService _dataAssetService;
        private IDepositProvider _depositProvider;
        private IProviderService _providerService;
        private ISessionService _sessionService;
        private IWallet _wallet;
        private IConsumerNotifier _consumerNotifier;
        private IConsumerSessionRepository _sessionRepository;
        private INdmPeer _providerPeer;
        private IDataStreamService _dataStreamService;

        [SetUp]
        public void Setup()
        {
            _dataAssetService = Substitute.For<IDataAssetService>();
            _depositProvider = Substitute.For<IDepositProvider>();
            _providerService = Substitute.For<IProviderService>();
            _sessionService = Substitute.For<ISessionService>();
            _wallet = Substitute.For<IWallet>();
            _consumerNotifier = Substitute.For<IConsumerNotifier>();
            _sessionRepository = Substitute.For<IConsumerSessionRepository>();
            _providerPeer = Substitute.For<INdmPeer>();
            _dataStreamService = new DataStreamService(_dataAssetService, _depositProvider, _providerService,
                _sessionService, _wallet, _consumerNotifier, _sessionRepository, LimboLogs.Instance);
        }
        
        [Test]
        public async Task disable_data_stream_should_fail_for_missing_session()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            
            var result = await _dataStreamService.DisableDataStreamAsync(depositId, client);
            
            result.Should().BeNull();
            _providerPeer.DidNotReceive().SendDisableDataStream(depositId, client);
            _sessionService.Received(1).GetActive(depositId);
            
        }

        [Test]
        public async Task disable_data_stream_should_fail_for_missing_provider()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            var session = GetConsumerSession();
            _sessionService.GetActive(depositId).Returns(session);
            
            var result = await _dataStreamService.DisableDataStreamAsync(depositId, client);
            
            result.Should().BeNull();
            _providerPeer.DidNotReceive().SendDisableDataStream(depositId, client);
            _sessionService.Received(1).GetActive(depositId);
            _providerService.Received(1).GetPeer(session.ProviderAddress);
        }
        
        [Test]
        public async Task disable_data_stream_should_fail_for_missing_deposit()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            var session = GetConsumerSession();
            _sessionService.GetActive(depositId).Returns(session);
            _providerService.GetPeer(session.ProviderAddress).Returns(_providerPeer);
            
            var result = await _dataStreamService.DisableDataStreamAsync(depositId, client);
            
            result.Should().BeNull();
            _providerPeer.DidNotReceive().SendDisableDataStream(depositId, client);
            _sessionService.Received(1).GetActive(depositId);
            _providerService.Received(1).GetPeer(session.ProviderAddress);
            await _depositProvider.Received(1).GetAsync(session.DepositId);
        }
        
        [Test]
        public async Task disable_data_stream_should_fail_for_locked_account()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            var session = GetConsumerSession();
            var deposit = GetDepositDetails();
            _sessionService.GetActive(depositId).Returns(session);
            _providerService.GetPeer(session.ProviderAddress).Returns(_providerPeer);
            _depositProvider.GetAsync(session.DepositId).Returns(deposit);
            
            var result = await _dataStreamService.DisableDataStreamAsync(depositId, client);
            
            result.Should().BeNull();
            _providerPeer.DidNotReceive().SendDisableDataStream(depositId, client);
            _sessionService.Received(1).GetActive(depositId);
            _providerService.Received(1).GetPeer(session.ProviderAddress);
            await _depositProvider.Received(1).GetAsync(session.DepositId);
            _wallet.Received(1).IsUnlocked(deposit.Consumer);
        }
        
        [Test]
        public async Task disable_data_stream_should_fail_for_missing_data_asset()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            var session = GetConsumerSession();
            var deposit = GetDepositDetails();
            _sessionService.GetActive(depositId).Returns(session);
            _providerService.GetPeer(session.ProviderAddress).Returns(_providerPeer);
            _depositProvider.GetAsync(session.DepositId).Returns(deposit);
            _wallet.IsUnlocked(deposit.Consumer).Returns(true);
            
            var result = await _dataStreamService.DisableDataStreamAsync(depositId, client);
            
            result.Should().BeNull();
            _providerPeer.DidNotReceive().SendDisableDataStream(depositId, client);
            _sessionService.Received(1).GetActive(depositId);
            _providerService.Received(1).GetPeer(session.ProviderAddress);
            await _depositProvider.Received(1).GetAsync(session.DepositId);
            _wallet.Received(1).IsUnlocked(deposit.Consumer);
            _dataAssetService.Received(1).GetDiscovered(deposit.DataAsset.Id);
        }
        
        [Test]
        public async Task disable_data_stream_should_fail_for_unavailable_data_asset()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            var session = GetConsumerSession();
            var deposit = GetDepositDetails();
            var dataAsset = GetDataAsset();
            _sessionService.GetActive(depositId).Returns(session);
            _providerService.GetPeer(session.ProviderAddress).Returns(_providerPeer);
            _depositProvider.GetAsync(session.DepositId).Returns(deposit);
            _wallet.IsUnlocked(deposit.Consumer).Returns(true);
            _dataAssetService.GetDiscovered(deposit.DataAsset.Id).Returns(dataAsset);
            
            var result = await _dataStreamService.DisableDataStreamAsync(depositId, client);
            
            result.Should().BeNull();
            _providerPeer.DidNotReceive().SendDisableDataStream(depositId, client);
            _sessionService.Received(1).GetActive(depositId);
            _providerService.Received(1).GetPeer(session.ProviderAddress);
            await _depositProvider.Received(1).GetAsync(session.DepositId);
            _wallet.Received(1).IsUnlocked(deposit.Consumer);
            _dataAssetService.Received(1).GetDiscovered(deposit.DataAsset.Id);
            _dataAssetService.Received(1).IsAvailable(dataAsset);
        }
        
        [Test]
        public async Task disable_data_stream_should_succeed_for_existing_deposit_and_unlocked_account_and_available_data_asset()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            var session = GetConsumerSession();
            var deposit = GetDepositDetails();
            var dataAsset = GetDataAsset();
            _sessionService.GetActive(depositId).Returns(session);
            _providerService.GetPeer(session.ProviderAddress).Returns(_providerPeer);
            _depositProvider.GetAsync(session.DepositId).Returns(deposit);
            _wallet.IsUnlocked(deposit.Consumer).Returns(true);
            _dataAssetService.GetDiscovered(deposit.DataAsset.Id).Returns(dataAsset);
            _dataAssetService.IsAvailable(dataAsset).Returns(true);
            
            var result = await _dataStreamService.DisableDataStreamAsync(depositId, client);
            
            result.Should().Be(depositId);
            _providerPeer.Received(1).SendDisableDataStream(depositId, client);
            _sessionService.Received(1).GetActive(depositId);
            _providerService.Received(1).GetPeer(session.ProviderAddress);
            await _depositProvider.Received(1).GetAsync(session.DepositId);
            _wallet.Received(1).IsUnlocked(deposit.Consumer);
            _dataAssetService.Received(1).GetDiscovered(deposit.DataAsset.Id);
            _dataAssetService.Received(1).IsAvailable(dataAsset);
        }
        
        [Test]
        public async Task disable_data_streams_should_fail_for_missing_session()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            
            var result = await _dataStreamService.DisableDataStreamsAsync(depositId);
            
            result.Should().BeNull();
            _sessionService.Received(1).GetActive(depositId);
            _providerPeer.DidNotReceive().SendDisableDataStream(depositId, client);
        }
        
        [Test]
        public async Task disable_data_streams_should_not_disable_any_streams_if_none_are_enabled()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            var session = GetConsumerSession();
            _sessionService.GetActive(depositId).Returns(session);
            
            var result = await _dataStreamService.DisableDataStreamsAsync(depositId);
            
            result.Should().Be(depositId);
            _sessionService.Received(1).GetActive(depositId);
            _providerPeer.DidNotReceive().SendDisableDataStream(depositId, client);
        }
        
        [Test]
        public async Task disable_data_streams_should_disable_all_enabled_streams()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            var args = Array.Empty<string>();
            var dataAsset = GetDataAsset();
            var deposit = GetDepositDetails();
            var session = GetConsumerSession();
            session.EnableStream(client, args);
            _sessionService.GetActive(depositId).Returns(session);
            _providerService.GetPeer(session.ProviderAddress).Returns(_providerPeer);
            _depositProvider.GetAsync(session.DepositId).Returns(deposit);
            _wallet.IsUnlocked(deposit.Consumer).Returns(true);
            _dataAssetService.GetDiscovered(deposit.DataAsset.Id).Returns(dataAsset);
            _dataAssetService.IsAvailable(dataAsset).Returns(true);
            
            var result = await _dataStreamService.DisableDataStreamsAsync(depositId);
            
            result.Should().Be(depositId);
            _sessionService.Received(2).GetActive(depositId);
            _providerPeer.Received(1).SendDisableDataStream(depositId, client);
        }

        [Test]
        public async Task enable_data_stream_should_fail_for_missing_session()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            var args = Array.Empty<string>();
            
            var result = await _dataStreamService.EnableDataStreamAsync(depositId, client, args);
            
            result.Should().BeNull();
            _providerPeer.DidNotReceive().SendEnableDataStream(depositId, client, args);
            _sessionService.Received(1).GetActive(depositId);
        }

        [Test]
        public async Task enable_data_stream_should_fail_for_missing_provider()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            var args = Array.Empty<string>();
            var session = GetConsumerSession();
            _sessionService.GetActive(depositId).Returns(session);
            
            var result = await _dataStreamService.EnableDataStreamAsync(depositId, client, args);
            
            result.Should().BeNull();
            _providerPeer.DidNotReceive().SendEnableDataStream(depositId, client, args);
            _sessionService.Received(1).GetActive(depositId);
            _providerService.Received(1).GetPeer(session.ProviderAddress);
        }
        
        [Test]
        public async Task enable_data_stream_should_fail_for_missing_deposit()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            var args = Array.Empty<string>();
            var session = GetConsumerSession();
            _sessionService.GetActive(depositId).Returns(session);
            _providerService.GetPeer(session.ProviderAddress).Returns(_providerPeer);
            
            var result = await _dataStreamService.EnableDataStreamAsync(depositId, client, args);
            
            result.Should().BeNull();
            _providerPeer.DidNotReceive().SendEnableDataStream(depositId, client, args);
            _sessionService.Received(1).GetActive(depositId);
            _providerService.Received(1).GetPeer(session.ProviderAddress);
            await _depositProvider.Received(1).GetAsync(session.DepositId);
        }
        
        [Test]
        public async Task enable_data_stream_should_fail_for_locked_account()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            var args = Array.Empty<string>();
            var session = GetConsumerSession();
            var deposit = GetDepositDetails();
            _sessionService.GetActive(depositId).Returns(session);
            _providerService.GetPeer(session.ProviderAddress).Returns(_providerPeer);
            _depositProvider.GetAsync(session.DepositId).Returns(deposit);
            
            var result = await _dataStreamService.EnableDataStreamAsync(depositId, client, args);
            
            result.Should().BeNull();
            _providerPeer.DidNotReceive().SendEnableDataStream(depositId, client, args);
            _sessionService.Received(1).GetActive(depositId);
            _providerService.Received(1).GetPeer(session.ProviderAddress);
            await _depositProvider.Received(1).GetAsync(session.DepositId);
            _wallet.Received(1).IsUnlocked(deposit.Consumer);
        }
        
        [Test]
        public async Task enable_data_stream_should_fail_for_missing_data_asset()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            var args = Array.Empty<string>();
            var session = GetConsumerSession();
            var deposit = GetDepositDetails();
            _sessionService.GetActive(depositId).Returns(session);
            _providerService.GetPeer(session.ProviderAddress).Returns(_providerPeer);
            _depositProvider.GetAsync(session.DepositId).Returns(deposit);
            _wallet.IsUnlocked(deposit.Consumer).Returns(true);
            
            var result = await _dataStreamService.EnableDataStreamAsync(depositId, client, args);
            
            result.Should().BeNull();
            _providerPeer.DidNotReceive().SendEnableDataStream(depositId, client, args);
            _sessionService.Received(1).GetActive(depositId);
            _providerService.Received(1).GetPeer(session.ProviderAddress);
            await _depositProvider.Received(1).GetAsync(session.DepositId);
            _wallet.Received(1).IsUnlocked(deposit.Consumer);
            _dataAssetService.Received(1).GetDiscovered(deposit.DataAsset.Id);
        }
        
        [Test]
        public async Task enable_data_stream_should_fail_for_unavailable_data_asset()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            var args = Array.Empty<string>();
            var session = GetConsumerSession();
            var deposit = GetDepositDetails();
            var dataAsset = GetDataAsset();
            _sessionService.GetActive(depositId).Returns(session);
            _providerService.GetPeer(session.ProviderAddress).Returns(_providerPeer);
            _depositProvider.GetAsync(session.DepositId).Returns(deposit);
            _wallet.IsUnlocked(deposit.Consumer).Returns(true);
            _dataAssetService.GetDiscovered(deposit.DataAsset.Id).Returns(dataAsset);
            
            var result = await _dataStreamService.EnableDataStreamAsync(depositId, client, args);
            
            result.Should().BeNull();
            _providerPeer.DidNotReceive().SendEnableDataStream(depositId, client, args);
            _sessionService.Received(1).GetActive(depositId);
            _providerService.Received(1).GetPeer(session.ProviderAddress);
            await _depositProvider.Received(1).GetAsync(session.DepositId);
            _wallet.Received(1).IsUnlocked(deposit.Consumer);
            _dataAssetService.Received(1).GetDiscovered(deposit.DataAsset.Id);
            _dataAssetService.Received(1).IsAvailable(dataAsset);
        }
        
        [Test]
        public async Task enable_data_stream_should_succeed_for_existing_deposit_and_unlocked_account_and_available_data_asset()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            var args = Array.Empty<string>();
            var session = GetConsumerSession();
            var deposit = GetDepositDetails();
            var dataAsset = GetDataAsset();
            _sessionService.GetActive(depositId).Returns(session);
            _providerService.GetPeer(session.ProviderAddress).Returns(_providerPeer);
            _depositProvider.GetAsync(session.DepositId).Returns(deposit);
            _wallet.IsUnlocked(deposit.Consumer).Returns(true);
            _dataAssetService.GetDiscovered(deposit.DataAsset.Id).Returns(dataAsset);
            _dataAssetService.IsAvailable(dataAsset).Returns(true);
            
            var result = await _dataStreamService.EnableDataStreamAsync(depositId, client, args);
            
            result.Should().Be(depositId);
            _providerPeer.Received(1).SendEnableDataStream(depositId, client, args);
            _sessionService.Received(1).GetActive(depositId);
            _providerService.Received(1).GetPeer(session.ProviderAddress);
            await _depositProvider.Received(1).GetAsync(session.DepositId);
            _wallet.Received(1).IsUnlocked(deposit.Consumer);
            _dataAssetService.Received(1).GetDiscovered(deposit.DataAsset.Id);
            _dataAssetService.Received(1).IsAvailable(dataAsset);
        }
        
        [Test]
        public async Task enable_data_stream_should_also_disable_already_existing_stream_if_exists()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            var args = Array.Empty<string>();
            var session = GetConsumerSession();
            var deposit = GetDepositDetails();
            var dataAsset = GetDataAsset();
            _sessionService.GetActive(depositId).Returns(session);
            _providerService.GetPeer(session.ProviderAddress).Returns(_providerPeer);
            _depositProvider.GetAsync(session.DepositId).Returns(deposit);
            _wallet.IsUnlocked(deposit.Consumer).Returns(true);
            _dataAssetService.GetDiscovered(deposit.DataAsset.Id).Returns(dataAsset);
            _dataAssetService.IsAvailable(dataAsset).Returns(true);
            
            session.EnableStream(client, args);
            var result = await _dataStreamService.EnableDataStreamAsync(depositId, client, args);
            
            result.Should().Be(depositId);
            _providerPeer.Received(1).SendDisableDataStream(depositId, client);
            _providerPeer.Received(1).SendEnableDataStream(depositId, client, args);
            _sessionService.Received(1).GetActive(depositId);
            _providerService.Received(1).GetPeer(session.ProviderAddress);
            await _depositProvider.Received(1).GetAsync(session.DepositId);
            _wallet.Received(1).IsUnlocked(deposit.Consumer);
            _dataAssetService.Received(1).GetDiscovered(deposit.DataAsset.Id);
            _dataAssetService.Received(1).IsAvailable(dataAsset);
        }
        
        [Test]
        public async Task set_enabled_data_stream_should_fail_for_missing_session()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            var args = Array.Empty<string>();
            var session = GetConsumerSession();
            
            await _dataStreamService.SetEnabledDataStreamAsync(depositId, client, args);
            
            session.Clients.Should().BeEmpty();
            await _sessionRepository.DidNotReceive().UpdateAsync(session);
            await _consumerNotifier.DidNotReceive().SendDataStreamEnabledAsync(depositId, session.Id);
        }
        
        [Test]
        public async Task set_enabled_data_stream_should_succeed_for_existing_session()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            var args = Array.Empty<string>();
            var session = GetConsumerSession();
            _sessionService.GetActive(depositId).Returns(session);
            
            await _dataStreamService.SetEnabledDataStreamAsync(depositId, client, args);

            session.Clients.Single().Should().Be(new SessionClient(client, true, args));
            await _sessionRepository.Received(1).UpdateAsync(session);
            await _consumerNotifier.Received(1).SendDataStreamEnabledAsync(depositId, session.Id);
        }
        
        [Test]
        public async Task set_disabled_data_stream_should_fail_for_missing_session()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            var args = Array.Empty<string>();
            var session = GetConsumerSession();
            
            await _dataStreamService.SetDisabledDataStreamAsync(depositId, client);
            
            session.Clients.Should().BeEmpty();
            await _sessionRepository.DidNotReceive().UpdateAsync(session);
            await _consumerNotifier.DidNotReceive().SendDataStreamDisabledAsync(depositId, session.Id);
        }
        
        [Test]
        public async Task set_disabled_data_stream_should_succeed_for_existing_session()
        {
            var depositId = Keccak.Zero;
            var client = "test";
            var args = Array.Empty<string>();
            var session = GetConsumerSession();
            session.EnableStream(client, args);
            var sessionClient = session.GetClient(client);
            _sessionService.GetActive(depositId).Returns(session);
            
            await _dataStreamService.SetDisabledDataStreamAsync(depositId, client);

            sessionClient.StreamEnabled.Should().BeFalse();
            session.Clients.Should().BeEmpty();
            await _sessionRepository.Received(1).UpdateAsync(session);
            await _consumerNotifier.Received(1).SendDataStreamDisabledAsync(depositId, session.Id);
        }

        private static ConsumerSession GetConsumerSession()
            => new ConsumerSession(Keccak.Zero, Keccak.Zero, Keccak.OfAnEmptyString, TestItem.AddressA,
                TestItem.PublicKeyA, TestItem.AddressB, TestItem.PublicKeyB, SessionState.Started,
                0, 0, dataAvailability: DataAvailability.Available);

        private static DepositDetails GetDepositDetails()
            => new DepositDetails(new Deposit(Keccak.Zero, 1, 1, 1),
                GetDataAsset(), TestItem.AddressB, Array.Empty<byte>(), 1,
                new []{TransactionInfo.Default(TestItem.KeccakA, 1, 1, 1, 1)}, 1);

        private static DataAsset GetDataAsset()
            => new DataAsset(Keccak.OfAnEmptyString, "test", "test", 1,
                DataAssetUnitType.Unit, 0, 10, new DataAssetRules(new DataAssetRule(1)),
                new DataAssetProvider(Address.Zero, "test"));
    }
}