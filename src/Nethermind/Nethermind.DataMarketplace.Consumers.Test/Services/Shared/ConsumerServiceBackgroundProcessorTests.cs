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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Databases;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Repositories;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Consumers.Notifiers.Services;
using Nethermind.DataMarketplace.Consumers.Refunds;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Consumers.Shared.Services;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Shared
{
    [TestFixture]
    public class ConsumerServiceBackgroundProcessorTests
    {
        private ConsumerServicesBackgroundProcessor _processor;
        private IBlockProcessor _blockProcessor;
        private DataAsset _asset;
        private Deposit _deposit;
        private Address _providerAddress = TestItem.AddressA;
        private DepositDetails _details;

        [SetUp]
        public void Setup()
        {
            DataAssetProvider provider = new DataAssetProvider(_providerAddress, "provider");
            _asset = new DataAsset(Keccak.Compute("1"), "name", "desc", 1, DataAssetUnitType.Unit, 1000, 10000, new DataAssetRules(new DataAssetRule(1)), provider, state: DataAssetState.Published);
            _deposit = new Deposit(Keccak.Zero, 1, 2, 3);
            _details = new DepositDetails(_deposit, _asset, Address.Zero, new byte[0], 1, new TransactionInfo[0]);

            IAccountService accountService = Substitute.For<IAccountService>();
            IRefundClaimant refundClaimant = Substitute.For<IRefundClaimant>();
            IDepositConfirmationService depositConfirmationService = Substitute.For<IDepositConfirmationService>();
            IEthPriceService ethPriceService = Substitute.For<IEthPriceService>();
            IGasPriceService gasPriceService = Substitute.For<IGasPriceService>();
            _blockProcessor = Substitute.For<IBlockProcessor>();
            IConsumerNotifier notifier = new ConsumerNotifier(Substitute.For<INdmNotifier>());
            IDepositDetailsRepository repository = new DepositDetailsInMemoryRepository(new DepositsInMemoryDb());
            repository.AddAsync(_details);
            _processor = new ConsumerServicesBackgroundProcessor(accountService, refundClaimant, depositConfirmationService, ethPriceService, gasPriceService, _blockProcessor, repository, notifier, LimboLogs.Instance);
        }

        [TearDown]
        public void TearDown()
        {
            _processor.Dispose();
        }

        [Test]
        public void Can_init()
        {
            _processor.Init();
        }

        [Test]
        public void Reacts_to_block_processed()
        {
            _processor.Init();
            _blockProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(Build.A.Block.TestObject));
        }
    }
}