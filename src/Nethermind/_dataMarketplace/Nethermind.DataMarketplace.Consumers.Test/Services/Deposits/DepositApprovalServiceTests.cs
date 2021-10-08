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

using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.DataAssets.Services;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Deposits.Services;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Databases;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Repositories;
using Nethermind.DataMarketplace.Consumers.Notifiers.Services;
using Nethermind.DataMarketplace.Consumers.Providers.Services;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Deposits
{
    [TestFixture]
    public class DepositApprovalServiceTests
    {
        private INdmNotifier _ndmNotifier;
        private DepositApprovalService _service;
        private ConsumerDepositApprovalInMemoryRepository _cdaRepo;

        private Keccak _newPendingAssetId = TestItem.KeccakA;
        private Keccak _pendingAssetId = TestItem.KeccakB;
        private Keccak _confirmedAssetId = TestItem.KeccakC;
        private Keccak _rejectedAssetId = TestItem.KeccakD;

        private Address _invalidConsumer = TestItem.AddressC;
        private Address _consumerAddress = TestItem.AddressA;
        private Address _providerAddress = TestItem.AddressB;

        private DepositApproval _pendingApproval;
        private DepositApproval _confirmedApproval;
        private DepositApproval _rejectedApproval;
        private ProviderService _providerService;
        private PublicKey _providerId;

        [SetUp]
        public void Setup()
        {
            INdmPeer peer = Substitute.For<INdmPeer>();
            peer.ProviderAddress.Returns(_providerAddress);
            _providerId = TestItem.PublicKeyB;
            peer.NodeId.Returns(_providerId);

            DataAssetProvider provider = new DataAssetProvider(_providerAddress, "name");

            DataAsset newPendingAsset = new DataAsset(_newPendingAssetId, "name", "desc", 1, DataAssetUnitType.Unit, 1000, 10000, new DataAssetRules(new DataAssetRule(1), null), provider);
            DataAsset pendingAsset = new DataAsset(_pendingAssetId, "name", "desc", 1, DataAssetUnitType.Unit, 1000, 10000, new DataAssetRules(new DataAssetRule(1), null), provider);
            DataAsset rejectedAsset = new DataAsset(_rejectedAssetId, "name", "desc", 1, DataAssetUnitType.Unit, 1000, 10000, new DataAssetRules(new DataAssetRule(1), null), provider);
            DataAsset confirmedAsset = new DataAsset(_confirmedAssetId, "name", "desc", 1, DataAssetUnitType.Unit, 1000, 10000, new DataAssetRules(new DataAssetRule(1), null), provider);

            DepositsInMemoryDb db = new DepositsInMemoryDb();
            ProviderInMemoryRepository providerRepository = new ProviderInMemoryRepository(db);
            _cdaRepo = new ConsumerDepositApprovalInMemoryRepository();
            _ndmNotifier = Substitute.For<INdmNotifier>();
            ConsumerNotifier notifier = new ConsumerNotifier(_ndmNotifier);
            DataAssetService dataAssetService = new DataAssetService(providerRepository, notifier, LimboLogs.Instance);

            dataAssetService.AddDiscovered(newPendingAsset, peer);
            dataAssetService.AddDiscovered(pendingAsset, peer);
            dataAssetService.AddDiscovered(rejectedAsset, peer);
            dataAssetService.AddDiscovered(confirmedAsset, peer);

            _providerService = new ProviderService(providerRepository, notifier, LimboLogs.Instance);
            _providerService.Add(peer);

            _service = new DepositApprovalService(dataAssetService, _providerService, _cdaRepo, Timestamper.Default, notifier, LimboLogs.Instance);

            _confirmedApproval = new DepositApproval(_confirmedAssetId, "asset", "kyc", _consumerAddress, _providerAddress, 1, DepositApprovalState.Confirmed);
            _pendingApproval = new DepositApproval(_pendingAssetId, "asset", "kyc", _consumerAddress, _providerAddress, 1, DepositApprovalState.Pending);
            _rejectedApproval = new DepositApproval(_rejectedAssetId, "asset", "kyc", _consumerAddress, _providerAddress, 1, DepositApprovalState.Rejected);

            _cdaRepo.AddAsync(_confirmedApproval);
            _cdaRepo.AddAsync(_pendingApproval);
            _cdaRepo.AddAsync(_rejectedApproval);
        }

        [Test]
        public async Task Can_browse()
        {
            var result = await _service.BrowseAsync(new GetConsumerDepositApprovals());
            result.Items.Should().HaveCount(3);
        }

        [Test]
        public async Task Can_browse_pending()
        {
            var result = await _service.BrowseAsync(new GetConsumerDepositApprovals {OnlyPending = true});
            result.Items.Should().HaveCount(1);
        }

        [Test]
        public async Task Can_browse_by_provider()
        {
            var result = await _service.BrowseAsync(new GetConsumerDepositApprovals {Provider = _providerAddress});
            result.Items.Should().HaveCount(3);
        }

        [Test]
        public async Task Can_request()
        {
            Keccak id = await _service.RequestAsync(_newPendingAssetId, _consumerAddress, "kyc");
            id.Should().NotBeNull();

            var result = await _service.BrowseAsync(new GetConsumerDepositApprovals());
            result.Items.Should().HaveCount(4);
        }

        [Test]
        public async Task Cannot_request_when_provider_is_missing()
        {
            _providerService.Remove(_providerId);
            Keccak id = await _service.RequestAsync(_newPendingAssetId, _consumerAddress, "kyc");
            id.Should().BeNull();
        }

        [Test]
        public async Task Can_reject()
        {
            Keccak id = await _service.RequestAsync(_pendingAssetId, _consumerAddress, "kyc");
            id.Should().NotBeNull();

            await _service.RejectAsync(_pendingAssetId, _consumerAddress);
            var result = await _service.BrowseAsync(new GetConsumerDepositApprovals());
            result.Items.Where(i => i.State == DepositApprovalState.Rejected).Should().HaveCount(2);
        }

        [Test]
        public async Task Ignore_reject_on_rejected()
        {
            Keccak id = await _service.RequestAsync(_rejectedAssetId, _consumerAddress, "kyc");
            id.Should().NotBeNull();

            await _service.RejectAsync(_rejectedAssetId, _consumerAddress);
            var result = await _service.BrowseAsync(new GetConsumerDepositApprovals());
            result.Items.Where(i => i.State == DepositApprovalState.Rejected).Should().HaveCount(1);
        }

        [Test]
        public async Task Can_approve()
        {
            Keccak id = await _service.RequestAsync(_pendingAssetId, _consumerAddress, "kyc");
            id.Should().NotBeNull();

            await _service.ConfirmAsync(_pendingAssetId, _consumerAddress);
            var result = await _service.BrowseAsync(new GetConsumerDepositApprovals());
            result.Items.Where(i => i.State == DepositApprovalState.Confirmed).Should().HaveCount(2);
        }

        [Test]
        public async Task Ignore_approve_on_approved()
        {
            Keccak id = await _service.RequestAsync(_confirmedAssetId, _consumerAddress, "kyc");
            id.Should().NotBeNull();

            await _service.ConfirmAsync(_confirmedAssetId, _consumerAddress);
            var result = await _service.BrowseAsync(new GetConsumerDepositApprovals());
            result.Items.Where(i => i.State == DepositApprovalState.Confirmed).Should().HaveCount(1);
        }

        [Test]
        public async Task Update_without_changes_will_proceed_without_errors()
        {
            await _service.UpdateAsync(new DepositApproval[] {_confirmedApproval, _rejectedApproval, _confirmedApproval}, _providerAddress);
            var result = await _service.BrowseAsync(new GetConsumerDepositApprovals());
            result.Items.Should().HaveCount(3);
        }

        [Test]
        public async Task Update_to_reject_will_proceed_without_errors()
        {
            DepositApproval updateToReject = new DepositApproval(_pendingApproval.AssetId, _pendingApproval.AssetName, _pendingApproval.Kyc, _pendingApproval.Consumer, _pendingApproval.Provider, _pendingApproval.Timestamp, DepositApprovalState.Rejected);
            await _service.UpdateAsync(new DepositApproval[] {updateToReject}, _providerAddress);
            var result = await _service.BrowseAsync(new GetConsumerDepositApprovals());
            result.Items.Where(i => i.State == DepositApprovalState.Rejected).Should().HaveCount(2);
        }

        [Test]
        public async Task Update_to_confirm_will_proceed_without_errors()
        {
            DepositApproval updateToConfirm = new DepositApproval(_pendingApproval.AssetId, _pendingApproval.AssetName, _pendingApproval.Kyc, _pendingApproval.Consumer, _pendingApproval.Provider, _pendingApproval.Timestamp, DepositApprovalState.Confirmed);
            await _service.UpdateAsync(new DepositApproval[] {updateToConfirm}, _providerAddress);
            var result = await _service.BrowseAsync(new GetConsumerDepositApprovals());
            result.Items.Where(i => i.State == DepositApprovalState.Confirmed).Should().HaveCount(2);
        }

        [Test]
        public async Task Update_to_pending_will_be_ignored()
        {
            DepositApproval updateToPending = new DepositApproval(_confirmedApproval.AssetId, _confirmedApproval.AssetName, _confirmedApproval.Kyc, _confirmedApproval.Consumer, _confirmedApproval.Provider, _confirmedApproval.Timestamp, DepositApprovalState.Pending);
            await _service.UpdateAsync(new DepositApproval[] {updateToPending}, _providerAddress);
            var result = await _service.BrowseAsync(new GetConsumerDepositApprovals());
            result.Items.Where(i => i.State == DepositApprovalState.Pending).Should().HaveCount(1);
        }

        [Test]
        public async Task Update_unknown_will_be_ignored()
        {
            DepositApproval unknown = new DepositApproval(_pendingApproval.AssetId, _pendingApproval.AssetName, _pendingApproval.Kyc, _pendingApproval.Consumer, _pendingApproval.Provider, _pendingApproval.Timestamp, DepositApprovalState.Confirmed);
            await _service.UpdateAsync(new DepositApproval[] {unknown}, _providerAddress);
            var result = await _service.BrowseAsync(new GetConsumerDepositApprovals());
            result.Items.Should().HaveCount(3);
        }

        [Test]
        public async Task Update_empty_will_proceed_without_errors()
        {
            await _service.UpdateAsync(new DepositApproval[0], _providerAddress);
            var result = await _service.BrowseAsync(new GetConsumerDepositApprovals());
            result.Items.Should().HaveCount(3);
        }

        [Test]
        public async Task Various_ignored_cases()
        {
            await _service.ConfirmAsync(_confirmedApproval.AssetId, _consumerAddress);
            await _service.RejectAsync(_rejectedApproval.AssetId, _consumerAddress);
            await _service.ConfirmAsync(Keccak.Compute("unknown"), _consumerAddress);
            await _service.RejectAsync(Keccak.Compute("unknown"), _consumerAddress);
            Keccak unknownAssetIdResult = await _service.RequestAsync(Keccak.Compute("unknown"), _consumerAddress, "kyc");
            unknownAssetIdResult.Should().BeNull();
            Keccak nullKycResult = await _service.RequestAsync(_newPendingAssetId, _consumerAddress, null);
            nullKycResult.Should().BeNull();
            Keccak emptyKycResult = await _service.RequestAsync(_newPendingAssetId, _consumerAddress, string.Empty);
            emptyKycResult.Should().BeNull();
            Keccak tooLongKycResult = await _service.RequestAsync(_newPendingAssetId, _consumerAddress, new byte[32 * 1024 / 2 + 1].ToHexString());
            tooLongKycResult.Should().BeNull();
        }
    }
}