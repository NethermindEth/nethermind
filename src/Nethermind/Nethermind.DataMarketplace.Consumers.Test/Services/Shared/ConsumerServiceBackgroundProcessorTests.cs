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
using System.Threading.Tasks;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Databases;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Repositories;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Consumers.Refunds;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Consumers.Shared.Services;
using Nethermind.DataMarketplace.Consumers.Shared.Services.Models;
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
        private IDepositDetailsRepository _depositRepository;
        IConsumerNotifier _consumerNotifier;
        IAccountService _accountService;
        IRefundClaimant _refundClaimant;

        [SetUp]
        public void Setup()
        {
            DataAssetProvider provider = new DataAssetProvider(_providerAddress, "provider");
            _asset = new DataAsset(Keccak.Compute("1"), "name", "desc", 1, DataAssetUnitType.Unit, 1000, 10000, new DataAssetRules(new DataAssetRule(1)), provider, state: DataAssetState.Published);
            _deposit = new Deposit(Keccak.Zero, 1, 2, 3);
            _details = new DepositDetails(_deposit, _asset, Address.Zero, new byte[0], 1, new TransactionInfo[0]);

            _accountService = Substitute.For<IAccountService>();
            _refundClaimant = Substitute.For<IRefundClaimant>();
            IDepositConfirmationService depositConfirmationService = Substitute.For<IDepositConfirmationService>();
            IPriceService priceService = Substitute.For<IPriceService>();
            IGasPriceService gasPriceService = Substitute.For<IGasPriceService>();
            IDepositUnitsCalculator depositUnitsCalculator = Substitute.For<IDepositUnitsCalculator>();
            _depositRepository = Substitute.For<IDepositDetailsRepository>();
            _blockProcessor = Substitute.For<IBlockProcessor>();
            _consumerNotifier = Substitute.For<IConsumerNotifier>();
            IDepositDetailsRepository repository = new DepositDetailsInMemoryRepository(new DepositsInMemoryDb(), depositUnitsCalculator);
            repository.AddAsync(_details);
            _processor = new ConsumerServicesBackgroundProcessor(_accountService, _refundClaimant, depositConfirmationService, gasPriceService, _blockProcessor, _depositRepository, _consumerNotifier, LimboLogs.Instance, priceService);
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
            _blockProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(Build.A.Block.TestObject, Array.Empty<TxReceipt>()));
        }

        [Test]
        // If deposits are eligable to refund (deposit is expired / consumer hasn't consumed all units yet etc.)
        // background proccessor will try to refund them every single block proccesed. 
        public void Will_try_to_refund_deposit_while_expired_and_not_consumed()
        {
            _processor.Init();
            var blockProccesed = Build.A.Block.TestObject;
            _accountService.GetAddress().Returns(TestItem.AddressB);

            var dataAsset = new DataAsset(Keccak.OfAnEmptyString, "test", "test", 1,
                DataAssetUnitType.Unit, 0, 10, new DataAssetRules(new DataAssetRule(1)),
                new DataAssetProvider(Address.Zero, "test"));

            _refundClaimant.TryClaimEarlyRefundAsync(Arg.Any<DepositDetails>(), TestItem.AddressB)
                .Returns(new RefundClaimStatus(Keccak.Zero, true));

            var consumedDeposit = new Deposit(Keccak.Zero, 10, 1, 1);
            var consumedDepositDetails = new DepositDetails(consumedDeposit, dataAsset, null, null, 1, null);
            consumedDepositDetails.SetConsumedUnits(9);

            var refundsResult = PagedResult<DepositDetails>.Create(
                new List<DepositDetails> { consumedDepositDetails }, 
                1, 
                1, 
                1, 
                1);

            _depositRepository.BrowseAsync(Arg.Any<GetDeposits>()).Returns(Task.FromResult(refundsResult));
            _blockProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(blockProccesed, Array.Empty<TxReceipt>()));
            _consumerNotifier.Received().SendClaimedEarlyRefundAsync(Arg.Any<Keccak>(), Arg.Any<string>(), Arg.Any<Keccak>());
        }

        [Test]
        // If all units are consumed, background processor should not try to refund this deposit.
        // Consumed units are not set in databases - they are calculated by DepositUnitsCalculator,
        // so we need to remember to always calculate consumed units after reading them from DB
        public void Will_not_try_to_refund_consumed_deposit()
        {
            _processor.Init();
            var blockProccesed = Build.A.Block.TestObject;

            var consumedDeposit = new Deposit(Keccak.Zero, 10, 1, 1);
            var consumedDepositDetails = new DepositDetails(consumedDeposit, null, null, null, 1, null);
            consumedDepositDetails.SetConsumedUnits(10);

            var refundsResult = PagedResult<DepositDetails>.Create(
                new List<DepositDetails> { consumedDepositDetails }, 
                1, 
                1, 
                1, 
                1);

            _depositRepository.BrowseAsync(Arg.Any<GetDeposits>()).Returns(Task.FromResult(refundsResult));
            _blockProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(blockProccesed, Array.Empty<TxReceipt>()));
            _consumerNotifier.DidNotReceive().SendClaimedRefundAsync(Arg.Any<Keccak>(), Arg.Any<string>(), Arg.Any<Keccak>());
        }
    }
}
