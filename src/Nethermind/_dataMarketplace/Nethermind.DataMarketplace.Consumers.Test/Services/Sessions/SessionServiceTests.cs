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

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.DataAssets.Services;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Databases;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Repositories;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Consumers.Notifiers.Services;
using Nethermind.DataMarketplace.Consumers.Providers;
using Nethermind.DataMarketplace.Consumers.Providers.Repositories;
using Nethermind.DataMarketplace.Consumers.Providers.Services;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Consumers.Sessions.Repositories;
using Nethermind.DataMarketplace.Consumers.Sessions.Services;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Sessions
{
    [TestFixture]
    public class SessionServiceTests
    {
        private SessionService _sessionService;
        private IConsumerSessionRepository _sessionRepository;

        private Keccak _session1Id = TestItem.KeccakA;
        private Keccak _session2Id = TestItem.KeccakB;

        private Keccak _deposit1Id = TestItem.KeccakA;
        private Deposit _deposit1;
        private DepositDetails _details1;

        private Keccak _deposit2Id = TestItem.KeccakB;
        private Deposit _deposit2;
        private DepositDetails _details2;

        private Keccak _depositForClosedId = TestItem.KeccakC;
        private Deposit _depositForClosed;
        private DepositDetails _depositForClosedDetails;

        private Keccak _depositForMissingId = TestItem.KeccakD;
        private Deposit _depositForMissing;
        private DepositDetails _depositForMissingDetails;

        private Keccak _missingDepositId = TestItem.KeccakE;

        private Keccak _asset1Id = TestItem.KeccakD;
        private DataAsset _asset1;

        private Keccak _asset2Id = TestItem.KeccakE;
        private DataAsset _asset2;

        private Keccak _missingAssetId = TestItem.KeccakG;

        private Keccak _closedId = TestItem.KeccakF;
        private DataAsset _closed;

        private Address _consumerAddress = TestItem.AddressA;
        private PublicKey _consumerNodeId = TestItem.PublicKeyA;

        private Address _providerAddress = TestItem.AddressB;
        private PublicKey _providerNodeId = TestItem.PublicKeyB;
        private INdmPeer _ndmPeer;
        private DataAsset _missingAsset;
        private IProviderService _providerService;

        [SetUp]
        public void Setup()
        {
            IConsumerNotifier notifier = new ConsumerNotifier(Substitute.For<INdmNotifier>());
            DepositsInMemoryDb db = new DepositsInMemoryDb();
            IProviderRepository providerRepository = new ProviderInMemoryRepository(db);
            DataAssetProvider provider = new DataAssetProvider(_providerAddress, "provider");
            DataAssetService dataAssetService = new DataAssetService(providerRepository, notifier, LimboLogs.Instance);

            _asset1 = new DataAsset(_asset1Id, "name", "desc", 1, DataAssetUnitType.Unit, 1000, 10000, new DataAssetRules(new DataAssetRule(1), new DataAssetRule(100)), provider, state: DataAssetState.Published);
            dataAssetService.AddDiscovered(_asset1, _ndmPeer);
            _deposit1 = new Deposit(_deposit1Id, 1, 2, 3);
            _details1 = new DepositDetails(_deposit1, _asset1, Address.Zero, new byte[0], 1, new TransactionInfo[0], 1);

            _asset2 = new DataAsset(_asset2Id, "name", "desc", 1, DataAssetUnitType.Time, 1000, 10000, new DataAssetRules(new DataAssetRule(1)), provider, state: DataAssetState.Published);
            dataAssetService.AddDiscovered(_asset2, _ndmPeer);
            _deposit2 = new Deposit(_deposit2Id, 1, 2, 3);
            _details2 = new DepositDetails(_deposit2, _asset2, Address.Zero, new byte[0], 1, new TransactionInfo[0], 2);

            _closed = new DataAsset(_closedId, "name", "desc", 1, DataAssetUnitType.Unit, 1000, 10000, new DataAssetRules(new DataAssetRule(1)), provider, state: DataAssetState.Closed);
            dataAssetService.AddDiscovered(_closed, _ndmPeer);
            _depositForClosed = new Deposit(_depositForClosedId, 1, 2, 3);
            _depositForClosedDetails = new DepositDetails(_depositForClosed, _closed, Address.Zero, new byte[0], 1, new TransactionInfo[0]);

            _missingAsset = new DataAsset(_missingAssetId, "name", "desc", 1, DataAssetUnitType.Unit, 1000, 10000, new DataAssetRules(new DataAssetRule(1)), provider, state: DataAssetState.Published);
            _depositForMissing = new Deposit(_depositForMissingId, 1, 2, 3);
            _depositForMissingDetails = new DepositDetails(_depositForMissing, _missingAsset, Address.Zero, new byte[0], 1, new TransactionInfo[0]);

            IDepositProvider depositProvider = Substitute.For<IDepositProvider>();
            depositProvider.GetAsync(_deposit1Id).Returns(_details1);
            depositProvider.GetAsync(_deposit2Id).Returns(_details2);
            depositProvider.GetAsync(_depositForMissingId).Returns(_depositForMissingDetails);
            depositProvider.GetAsync(_depositForClosedId).Returns(_depositForClosedDetails);

            _ndmPeer = Substitute.For<INdmPeer>();
            _ndmPeer.ProviderAddress.Returns(_providerAddress);
            _ndmPeer.NodeId.Returns(_providerNodeId);
            
            _providerService = new ProviderService(providerRepository, notifier, LimboLogs.Instance);
            _providerService.Add(_ndmPeer);

            _sessionRepository = new ConsumerSessionInMemoryRepository();
            _sessionService = new SessionService(_providerService, depositProvider, dataAssetService, _sessionRepository, Timestamper.Default, notifier, LimboLogs.Instance);
        }

        [Test]
        public void When_no_sessions_returns_empty_list()
        {
            var result = _sessionService.GetAllActive();
            result.Should().HaveCount(0);
        }

        [Test]
        public async Task Can_retrieve_all_active_sessions()
        {
            ConsumerSession consumerSession1 = new ConsumerSession(_session1Id, _deposit1Id, _asset1Id, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 4);
            ConsumerSession consumerSession2 = new ConsumerSession(_session2Id, _deposit2Id, _asset2Id, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 4);
            await _sessionService.StartSessionAsync(consumerSession1, _ndmPeer);
            await _sessionService.StartSessionAsync(consumerSession2, _ndmPeer);

            var result = _sessionService.GetAllActive();
            result.Should().HaveCount(2);
        }

        [Test]
        public async Task Can_retrieve_all_active_sessions_when_one_expired()
        {
            ConsumerSession consumerSession1 = new ConsumerSession(_session1Id, _deposit1Id, _asset1Id, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 4);
            ConsumerSession consumerSession2 = new ConsumerSession(_session2Id, _deposit1Id, _asset1Id, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 4);
            await _sessionService.StartSessionAsync(consumerSession1, _ndmPeer);
            await _sessionService.StartSessionAsync(consumerSession2, _ndmPeer);

            var result = _sessionService.GetAllActive();
            result.Should().HaveCount(1);
        }

        [Test]
        public async Task Adds_upfront_units_to_session_on_start()
        {
            ConsumerSession consumerSession = new ConsumerSession(_session1Id, _deposit1Id, _asset1Id, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 4);
            await _sessionService.StartSessionAsync(consumerSession, _ndmPeer);
            var result = _sessionService.GetActive(_deposit1Id);
            result.UnpaidUnits.Should().Be((uint) _asset1.Rules.UpfrontPayment.Value);
        }

        [Test]
        public async Task Adds_unpaid_units_based_on_time_between_session_and_confirmation()
        {
            ConsumerSession consumerSession = new ConsumerSession(_session1Id, _deposit2Id, _asset2Id, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 4);
            await _sessionService.StartSessionAsync(consumerSession, _ndmPeer);
            var result = _sessionService.GetActive(_deposit2Id);
            result.Should().NotBeNull();
            result.UnpaidUnits.Should().Be((uint)(consumerSession.StartTimestamp - _details2.ConfirmationTimestamp));
        }

        [Test]
        public async Task Can_start_session()
        {
            ConsumerSession consumerSession = new ConsumerSession(_session1Id, _deposit1Id, _asset1Id, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 4);
            await _sessionService.StartSessionAsync(consumerSession, _ndmPeer);
            var result = _sessionService.GetActive(_deposit1Id);
            result.Should().NotBeNull();
        }
        
        [Test]
        public async Task Can_request_session_finish()
        {
            ConsumerSession consumerSession = new ConsumerSession(_session1Id, _deposit1Id, _asset1Id, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 4);
            await _sessionService.StartSessionAsync(consumerSession, _ndmPeer);
            await _sessionService.SendFinishSessionAsync(_deposit1Id);
            _ndmPeer.Received().SendFinishSession(_deposit1Id);
        }
        
        [Test]
        public async Task Can_handle_request_session_finish_even_when_peer_is_missing()
        {
            ConsumerSession consumerSession = new ConsumerSession(_session1Id, _deposit1Id, _asset1Id, _consumerAddress, _consumerNodeId, TestItem.AddressD, _providerNodeId, SessionState.Started, 1, 2, 4);
            INdmPeer peer = Substitute.For<INdmPeer>();
            peer.ProviderAddress.Returns(TestItem.AddressD);
            await _sessionService.StartSessionAsync(consumerSession, peer);
            await _sessionService.SendFinishSessionAsync(_deposit1Id);
            _ndmPeer.DidNotReceive().SendFinishSession(_deposit1Id);
        }
        
        [Test]
        public async Task Can_handle_request_session_finish_even_when_deposit_is_unknown()
        {
            ConsumerSession consumerSession = new ConsumerSession(_session1Id, _missingDepositId, _asset1Id, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 4);
            await _sessionService.SendFinishSessionAsync(_missingDepositId);
            _ndmPeer.DidNotReceive().SendFinishSession(_deposit1Id);
        }
        
        [Test]
        public async Task Can_handle_request_session_finish_even_when_there_is_no_session()
        {
            await _sessionService.SendFinishSessionAsync(_deposit1Id);
            _ndmPeer.DidNotReceive().SendFinishSession(_deposit1Id);
        }
        
        [Test]
        public async Task Can_finish_session_without_removing_provider()
        {
            ConsumerSession consumerSession = new ConsumerSession(_session1Id, _deposit1Id, _asset1Id, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 4);
            await _sessionService.StartSessionAsync(consumerSession, _ndmPeer);
            await _sessionService.FinishSessionAsync(consumerSession, _ndmPeer, false);
            var result = _sessionService.GetActive(_deposit1Id);
            result.Should().BeNull();
            _providerService.GetPeer(_ndmPeer.ProviderAddress).Should().NotBeNull();
        }
        
        [Test]
        public async Task Can_finish_session_and_remove_provider()
        {
            ConsumerSession consumerSession = new ConsumerSession(_session1Id, _deposit1Id, _asset1Id, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 4);
            await _sessionService.StartSessionAsync(consumerSession, _ndmPeer);
            await _sessionService.FinishSessionAsync(consumerSession, _ndmPeer, true);
            var result = _sessionService.GetActive(_deposit1Id);
            result.Should().BeNull();
            _providerService.GetPeer(_ndmPeer.ProviderAddress).Should().BeNull();
        }

        [Test]
        public async Task Can_finish_session_and_remove_provider_even_when_session_is_missing()
        {
            ConsumerSession consumerSession = new ConsumerSession(_session1Id, _deposit1Id, _asset1Id, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 4);
            await _sessionService.StartSessionAsync(consumerSession, _ndmPeer);
            await _sessionService.FinishSessionAsync(consumerSession, _ndmPeer, false);
            await _sessionService.FinishSessionAsync(consumerSession, _ndmPeer, true);
            var result = _sessionService.GetActive(_deposit1Id);
            result.Should().BeNull();
            _providerService.GetPeer(_ndmPeer.ProviderAddress).Should().BeNull();
        }
        
        [Test]
        public async Task Can_finish_provider_sessions_without_removing_provider()
        {
            ConsumerSession consumerSession = new ConsumerSession(_session1Id, _deposit1Id, _asset1Id, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 4);
            await _sessionService.StartSessionAsync(consumerSession, _ndmPeer);
            await _sessionService.FinishSessionsAsync(_ndmPeer, false);
            var result = _sessionService.GetActive(_deposit1Id);
            result.Should().BeNull();
            _providerService.GetPeer(_ndmPeer.ProviderAddress).Should().NotBeNull();
        }
        
        [Test]
        public async Task Will_not_finish_sessions_from_other_providers()
        {
            ConsumerSession consumerSession = new ConsumerSession(_session1Id, _deposit1Id, _asset1Id, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 4);
            await _sessionService.StartSessionAsync(consumerSession, _ndmPeer);

            INdmPeer otherPeer = Substitute.For<INdmPeer>();
            otherPeer.ProviderAddress.Returns(TestItem.AddressD);
            otherPeer.NodeId.Returns(TestItem.PublicKeyD);
            await _sessionService.FinishSessionsAsync(otherPeer, true);
            var result = _sessionService.GetActive(_deposit1Id);
            result.Should().NotBeNull();
            _providerService.GetPeer(_ndmPeer.ProviderAddress).Should().NotBeNull();
        }
        
        [Test]
        public async Task Can_finish_provider_sessions_and_remove_provider()
        {
            ConsumerSession consumerSession = new ConsumerSession(_session1Id, _deposit1Id, _asset1Id, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 4);
            await _sessionService.StartSessionAsync(consumerSession, _ndmPeer);
            await _sessionService.FinishSessionsAsync(_ndmPeer, true);
            var result = _sessionService.GetActive(_deposit1Id);
            result.Should().BeNull();
            _providerService.GetPeer(_ndmPeer.ProviderAddress).Should().BeNull();
        }

        [Test]
        public async Task Cannot_start_session_with_timestamp_before_confirmation_timestamp()
        {
            ConsumerSession consumerSession = new ConsumerSession(_session1Id, _deposit1Id, _asset1Id, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 0);
            await _sessionService.StartSessionAsync(consumerSession, _ndmPeer);
            var result = _sessionService.GetActive(_deposit1Id);
            result.Should().BeNull();
        }
        
        [Test]
        public async Task Cannot_start_session_when_data_asset_ids_do_not_match_between_session_and_deposit()
        {
            ConsumerSession consumerSession = new ConsumerSession(_session1Id, _deposit1Id, _asset2Id, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 4);
            await _sessionService.StartSessionAsync(consumerSession, _ndmPeer);
            var result = _sessionService.GetActive(_deposit1Id);
            result.Should().BeNull();
        }

        [Test]
        public async Task Cannot_start_session_for_a_closed_data_asset()
        {
            ConsumerSession consumerSession = new ConsumerSession(_session1Id, _depositForClosedId, _closedId, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 4);
            await _sessionService.StartSessionAsync(consumerSession, _ndmPeer);
            var result = _sessionService.GetActive(_depositForClosedId);
            result.Should().BeNull();
        }

        [Test]
        public async Task Cannot_start_session_for_an_unknown_data_asset()
        {
            ConsumerSession consumerSession = new ConsumerSession(_session1Id, _depositForMissingId, _missingAssetId, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 4);
            await _sessionService.StartSessionAsync(consumerSession, _ndmPeer);
            var result = _sessionService.GetActive(_depositForMissingId);
            result.Should().BeNull();
        }

        [Test]
        public async Task Cannot_start_session_for_an_unknown_deposit()
        {
            ConsumerSession consumerSession = new ConsumerSession(_session1Id, _missingDepositId, _missingAssetId, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 4);
            await _sessionService.StartSessionAsync(consumerSession, _ndmPeer);
            var result = _sessionService.GetActive(_missingDepositId);
            result.Should().BeNull();
        }

        [Test]
        public async Task Cannot_start_session_for_an_unknown_provider()
        {
            ConsumerSession consumerSession = new ConsumerSession(_session1Id, _deposit1Id, _asset1Id, _consumerAddress, _consumerNodeId, _providerAddress, _providerNodeId, SessionState.Started, 1, 2, 4);
            INdmPeer mismatchedPeer = Substitute.For<INdmPeer>();
            mismatchedPeer.ProviderAddress.Returns(TestItem.AddressD);
            mismatchedPeer.NodeId.Returns(TestItem.PublicKeyD);
            await _sessionService.StartSessionAsync(consumerSession, mismatchedPeer);
            var result = _sessionService.GetActive(_missingDepositId);
            result.Should().BeNull();
        }

        [Test]
        public async Task Cannot_start_session_for_a_null_provider()
        {
            ConsumerSession consumerSession = new ConsumerSession(_session1Id, _deposit1Id, _asset1Id, _consumerAddress, _consumerNodeId, null, _providerNodeId, SessionState.Started, 1, 2, 4);
            await _sessionService.StartSessionAsync(consumerSession, _ndmPeer);
            var result = _sessionService.GetActive(_missingDepositId);
            result.Should().BeNull();
        }
    }
}
