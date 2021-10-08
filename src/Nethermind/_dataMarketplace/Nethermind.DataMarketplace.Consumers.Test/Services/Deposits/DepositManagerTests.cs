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
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Consumers.DataAssets.Services;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Deposits.Services;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Databases;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Repositories;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Consumers.Notifiers.Services;
using Nethermind.DataMarketplace.Consumers.Providers.Repositories;
using Nethermind.DataMarketplace.Consumers.Providers.Services;
using Nethermind.DataMarketplace.Consumers.Sessions.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Logging;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Deposits
{
    [TestFixture]
    public class DepositManagerTests
    {
        private DepositManager _depositManager;
        private Deposit _deposit;
        private DepositDetails _details;
        private DataAsset _asset;
        private DataAsset _assetUnderMaintenance;
        private DataAsset _closedAsset;
        private DataAsset _withKyc;

        private IWallet _wallet;
        private Address _providerAddress = TestItem.AddressA;
        private IKycVerifier _kycVerifier;

        [SetUp]
        public void Setup()
        {
            DataAssetProvider provider = new DataAssetProvider(_providerAddress, "provider");
            _asset = new DataAsset(Keccak.Compute("1"), "name", "desc", 1, DataAssetUnitType.Unit, 1000, 10000, new DataAssetRules(new DataAssetRule(1)), provider, state: DataAssetState.Published);
            _closedAsset = new DataAsset(Keccak.Compute("2"), "name", "desc", 1, DataAssetUnitType.Unit, 1000, 10000, new DataAssetRules(new DataAssetRule(1)), provider, state: DataAssetState.Closed);
            _assetUnderMaintenance = new DataAsset(Keccak.Compute("3"), "name", "desc", 1, DataAssetUnitType.Unit, 1000, 10000, new DataAssetRules(new DataAssetRule(1)), provider, state: DataAssetState.UnderMaintenance);
            _withKyc = new DataAsset(Keccak.Compute("4"), "name", "desc", 1, DataAssetUnitType.Unit, 1000, 10000, new DataAssetRules(new DataAssetRule(1)), provider, state: DataAssetState.Published, kycRequired: true);

            _deposit = new Deposit(Keccak.Zero, 1, 2, 3);
            _details = new DepositDetails(_deposit, _asset, Address.Zero, new byte[0], 1, new TransactionInfo[0]);

            INdmBlockchainBridge blockchainBridge = BlockchainBridgeBuilder.BuildABridge();
            _wallet = new DevWallet(new WalletConfig(), LimboLogs.Instance);
            DepositService depositService = new DepositService(blockchainBridge, new AbiEncoder(), _wallet, Address.Zero);
            IConsumerSessionRepository sessionRepository = new ConsumerSessionInMemoryRepository();
            DepositUnitsCalculator unitsCalculator = new DepositUnitsCalculator(sessionRepository, Timestamper.Default);
            DepositsInMemoryDb depositsInMemoryDb = new DepositsInMemoryDb();
            depositsInMemoryDb.Add(_details);

            IProviderRepository providerRepository = new ProviderInMemoryRepository(depositsInMemoryDb);
            IConsumerNotifier notifier = new ConsumerNotifier(Substitute.For<INdmNotifier>());
            DataAssetService dataAssetService = new DataAssetService(providerRepository, notifier, LimboLogs.Instance);
            IDepositUnitsCalculator depositUnitsCalculator = Substitute.For<IDepositUnitsCalculator>();
            INdmPeer peer = Substitute.For<INdmPeer>();
            peer.NodeId.Returns(TestItem.PublicKeyB);
            peer.ProviderAddress.Returns(_providerAddress);

            dataAssetService.AddDiscovered(_asset, peer);
            dataAssetService.AddDiscovered(_closedAsset, peer);
            dataAssetService.AddDiscovered(_assetUnderMaintenance, peer);
            dataAssetService.AddDiscovered(_withKyc, peer);
            
            _kycVerifier = Substitute.For<IKycVerifier>();
            ProviderService providerService = new ProviderService(providerRepository, notifier, LimboLogs.Instance);
            providerService.Add(peer);

            _depositManager = new DepositManager(depositService, unitsCalculator, dataAssetService, _kycVerifier, providerService, new AbiEncoder(), new CryptoRandom(), _wallet, Substitute.For<IGasPriceService>(), new DepositDetailsInMemoryRepository(depositsInMemoryDb, depositUnitsCalculator), Timestamper.Default, LimboLogs.Instance, 6, false);
        }

        [Test]
        public void Can_browse_deposits()
        {
            var result = _depositManager.BrowseAsync(new GetDeposits());
            result.Result.Items.Should().HaveCount(1);
        }

        [Test]
        public void Can_get_single_deposit()
        {
            var result = _depositManager.GetAsync(_deposit.Id);
            result.Result.Should().NotBeNull();
        }

        [Test]
        public void Returns_null_when_getting_non_existing_deposit()
        {
            var result = _depositManager.GetAsync(Keccak.Compute("unknown"));
            result.Result.Should().BeNull();
        }

        [Test]
        public async Task Can_make_deposit()
        {
            Address account = _wallet.GetAccounts()[0];
            Keccak result = await _depositManager.MakeAsync(_asset.Id, _asset.MinUnits, _asset.MinUnits * _asset.UnitPrice, account, 20.GWei());
            result.Should().NotBeNull();
        }
        
        [Test]
        public async Task Can_make_deposit_with_kyc()
        {
            Address account = _wallet.GetAccounts()[0];
            _kycVerifier.IsVerifiedAsync(null, null).ReturnsForAnyArgs(true);
            Keccak result = await _depositManager.MakeAsync(_withKyc.Id, _withKyc.MinUnits, _withKyc.MinUnits * _withKyc.UnitPrice, account, 20.GWei());
            result.Should().NotBeNull();
        }
        
        [Test]
        public async Task Cannot_make_deposit_when_kyc_is_not_satisfied()
        {
            Address account = _wallet.GetAccounts()[0];
            _kycVerifier.IsVerifiedAsync(null, null).ReturnsForAnyArgs(false);
            Keccak result = await _depositManager.MakeAsync(_withKyc.Id, _withKyc.MinUnits, _withKyc.MinUnits * _withKyc.UnitPrice, account, 20.GWei());
            result.Should().BeNull();
        }

        [Test]
        public async Task Can_make_deposit_when_asset_is_under_maintenance()
        {
            Address account = _wallet.GetAccounts()[0];
            Keccak result = await _depositManager.MakeAsync(_assetUnderMaintenance.Id, _assetUnderMaintenance.MinUnits, _assetUnderMaintenance.MinUnits * _assetUnderMaintenance.UnitPrice, account, 20.GWei());
            result.Should().NotBeNull();
        }

        [Test]
        public async Task Cannot_make_deposit_on_unknown_asset()
        {
            Address account = _wallet.GetAccounts()[0];
            Keccak result = await _depositManager.MakeAsync(Keccak.Compute("unknown"), _asset.MinUnits, _asset.MinUnits * _asset.UnitPrice, account, 20.GWei());
            result.Should().BeNull();
        }

        [Test]
        public async Task Cannot_make_deposit_on_closed_asset()
        {
            Address account = _wallet.GetAccounts()[0];
            Keccak result = await _depositManager.MakeAsync(_closedAsset.Id, _closedAsset.MinUnits, _closedAsset.MinUnits * _closedAsset.UnitPrice, account, 20.GWei());
            result.Should().BeNull();
        }

        [Test]
        public async Task Cannot_make_deposit_below_min_units()
        {
            Address account = _wallet.GetAccounts()[0];
            Keccak result = await _depositManager.MakeAsync(_asset.Id, _asset.MinUnits - 1, (_asset.MinUnits - 1) * _asset.UnitPrice, account, 20.GWei());
            result.Should().BeNull();
        }

        [Test]
        public async Task Cannot_make_deposit_above_max_units()
        {
            Address account = _wallet.GetAccounts()[0];
            Keccak result = await _depositManager.MakeAsync(_asset.Id, _asset.MaxUnits + 1, (_asset.MaxUnits + 1) * _asset.UnitPrice, account, 20.GWei());
            result.Should().BeNull();
        }

        [Test]
        public async Task Cannot_make_deposit_with_wrong_value()
        {
            Address account = _wallet.GetAccounts()[0];
            Keccak result = await _depositManager.MakeAsync(_asset.Id, _asset.MinUnits, _asset.MinUnits * _asset.UnitPrice - 1, account, 20.GWei());
            result.Should().BeNull();
        }

        [Test]
        public async Task Cannot_make_deposit_when_address_is_locked()
        {
            Address account = _wallet.GetAccounts()[0];
            _wallet.LockAccount(account);
            Keccak result = await _depositManager.MakeAsync(_asset.Id, 1000, 1, account, 20.GWei());
            result.Should().BeNull();
        }
    }
}
