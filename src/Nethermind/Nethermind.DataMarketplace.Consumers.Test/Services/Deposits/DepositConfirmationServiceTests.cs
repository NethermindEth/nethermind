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
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Consumers.Deposits.Services;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Consumers.Notifiers.Services;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Deposits
{
    public class DepositConfirmationServiceTests
    {
        private IDepositConfirmationService _depositConfirmationService;
        private INdmBlockchainBridge _blockchainBridge;
        private IConsumerNotifier _consumerNotifier;
        private IDepositDetailsRepository _depositRepository;
        private IDepositService _depositService;
        private uint _requiredBlockConfirmations;

        [SetUp]
        public void Setup()
        {
            _blockchainBridge = Substitute.For<INdmBlockchainBridge>();
            _consumerNotifier = Substitute.For<IConsumerNotifier>();
            _depositRepository = Substitute.For<IDepositDetailsRepository>();
            _depositService = Substitute.For<IDepositService>();
            _requiredBlockConfirmations = 2;
            _depositConfirmationService = new DepositConfirmationService(_blockchainBridge, _consumerNotifier,
                _depositRepository, _depositService, LimboLogs.Instance, _requiredBlockConfirmations);
        }

        [Test]
        public async Task try_confirm_should_not_confirm_deposit_if_it_is_already_confirmed()
        {
            var deposit = GetDepositDetails();
            deposit.SetConfirmations(1);
            deposit.SetConfirmationTimestamp(1);
            deposit.AddTransaction(TransactionInfo.Default(TestItem.KeccakA, 1, 1, 1, 1));
            await _depositConfirmationService.TryConfirmAsync(deposit);
            await _blockchainBridge.DidNotReceive().GetTransactionAsync(deposit.Transaction.Hash);
            deposit.Confirmed.Should().BeTrue();
        }
        
        [Test]
        public async Task try_confirm_should_not_confirm_deposit_if_it_is_already_rejected()
        {
            var deposit = GetDepositDetails();
            deposit.Reject();
            await _depositConfirmationService.TryConfirmAsync(deposit);
            await _blockchainBridge.DidNotReceive().GetTransactionAsync(deposit.Transaction.Hash);
            deposit.Confirmed.Should().BeFalse();
            deposit.Rejected.Should().BeTrue();
        }
        
        [Test]
        public async Task try_confirm_should_not_confirm_deposit_if_transaction_was_not_found()
        {
            var deposit = GetDepositDetails();
            await _depositConfirmationService.TryConfirmAsync(deposit);
            await _blockchainBridge.Received().GetTransactionAsync(deposit.Transaction.Hash);
            await _blockchainBridge.DidNotReceive().GetLatestBlockNumberAsync();
            deposit.Confirmed.Should().BeFalse();
        }
        
        [Test]
        public async Task try_confirm_should_not_confirm_deposit_if_transaction_is_pending()
        {
            var deposit = GetDepositDetails();
            var transaction = GetTransaction(true);
            _blockchainBridge.GetTransactionAsync(deposit.Transaction.Hash).Returns(transaction);
            await _depositConfirmationService.TryConfirmAsync(deposit);
            deposit.Confirmed.Should().BeFalse();
            deposit.Transaction.State.Should().Be(TransactionState.Pending);
            await _blockchainBridge.Received().GetTransactionAsync(deposit.Transaction.Hash);
        }

        [Test]
        public async Task try_confirm_should_not_confirm_deposit_if_transaction_is_of_type_cancellation()
        {
            var transactionDetails = GetTransaction();
            var transaction = transactionDetails.Transaction;
            var deposit = GetDepositDetails(transactions: new[]
            {
                new TransactionInfo(transaction.Hash, transaction.Value, transaction.GasPrice,
                    (ulong) transaction.GasLimit,
                    (ulong) transaction.Timestamp, TransactionType.Cancellation, TransactionState.Included)
            });
            _blockchainBridge.GetTransactionAsync(deposit.Transaction.Hash).Returns(transactionDetails);
            await _depositConfirmationService.TryConfirmAsync(deposit);
            deposit.Confirmed.Should().BeFalse();
            deposit.Transaction.Type.Should().Be(TransactionType.Cancellation);
            await _blockchainBridge.DidNotReceive().GetTransactionAsync(deposit.Transaction.Hash);
        }
        
        [Test]
        public async Task try_confirm_should_not_confirm_deposit_if_transaction_is_set_as_included_but_was_not_found()
        {
            var transactionDetails = GetTransaction();
            var transaction = transactionDetails.Transaction;
            transaction.Hash = Keccak.Zero;
            
            var deposit = GetDepositDetails(transactions: new[]
            {
                new TransactionInfo(transaction.Hash, transaction.Value, transaction.GasPrice,
                    (ulong) transaction.GasLimit,
                    (ulong) transaction.Timestamp, TransactionType.Default, TransactionState.Included)
            });
            _blockchainBridge.GetTransactionAsync(deposit.Transaction.Hash).Returns(transactionDetails);
            await _depositConfirmationService.TryConfirmAsync(deposit);
            deposit.Confirmed.Should().BeFalse();
            await _blockchainBridge.Received().GetTransactionAsync(deposit.Transaction.Hash);
        }

        [Test]
        public async Task try_confirm_should_set_transaction_state_to_included_if_was_pending_before()
        {
            var deposit = GetDepositDetails();
            var transaction = GetTransaction();
            _blockchainBridge.GetTransactionAsync(deposit.Transaction.Hash).Returns(transaction);
            await _depositConfirmationService.TryConfirmAsync(deposit);
            deposit.Transaction.State.Should().Be(TransactionState.Included);
            await _blockchainBridge.Received().GetTransactionAsync(deposit.Transaction.Hash);
            deposit.Confirmed.Should().BeFalse();
        }
        
        [Test]
        public async Task try_confirm_should_skip_further_processing_if_block_was_not_found()
        {
            const int latestBlockNumber = 3;
            var deposit = GetDepositDetails();
            var transaction = GetTransaction();
            _blockchainBridge.GetTransactionAsync(deposit.Transaction.Hash).Returns(transaction);
            _blockchainBridge.GetLatestBlockNumberAsync().Returns(latestBlockNumber);
            await _depositConfirmationService.TryConfirmAsync(deposit);
            await _blockchainBridge.Received().GetTransactionAsync(deposit.Transaction.Hash);
            await _blockchainBridge.Received().GetLatestBlockNumberAsync();
            await _blockchainBridge.Received().FindBlockAsync(latestBlockNumber);
            await _depositService.DidNotReceive().VerifyDepositAsync(deposit.Consumer, deposit.Id, Arg.Any<long>());
        }
        
        [Test]
        public async Task try_confirm_should_skip_further_processing_if_confirmation_timestamp_is_0()
        {
            const int latestBlockNumber = 3;
            const uint confirmationTimestamp = 0;
            var block = GetBlock();
            var deposit = GetDepositDetails();
            var transaction = GetTransaction();
            _blockchainBridge.GetTransactionAsync(deposit.Transaction.Hash).Returns(transaction);
            _blockchainBridge.GetLatestBlockNumberAsync().Returns(latestBlockNumber);
            _blockchainBridge.FindBlockAsync(latestBlockNumber).Returns(block);
            _depositService.VerifyDepositAsync(deposit.Consumer, deposit.Id, block.Header.Number)
                .Returns(confirmationTimestamp);
            await _depositConfirmationService.TryConfirmAsync(deposit);
            await _depositService.Received().VerifyDepositAsync(deposit.Consumer, deposit.Id, block.Header.Number);
            await _depositRepository.Received().UpdateAsync(deposit);
        }
        
        [Test]
        public async Task try_confirm_should_set_confirmation_timestamp_for_the_first_time_if_is_greater_than_0()
        {
            const int latestBlockNumber = 3;
            const uint confirmationTimestamp = 1;
            var block = GetBlock();
            var deposit = GetDepositDetails();
            var transaction = GetTransaction();
            _blockchainBridge.GetTransactionAsync(deposit.Transaction.Hash).Returns(transaction);
            _blockchainBridge.GetLatestBlockNumberAsync().Returns(latestBlockNumber);
            _blockchainBridge.FindBlockAsync(latestBlockNumber).Returns(block);
            _depositService.VerifyDepositAsync(deposit.Consumer, deposit.Id, block.Header.Number)
                .Returns(confirmationTimestamp);
            await _depositConfirmationService.TryConfirmAsync(deposit);
            await _depositRepository.Received().UpdateAsync(deposit);
            deposit.ConfirmationTimestamp.Should().Be(confirmationTimestamp);
        }
        
        [Test]
        public async Task try_confirm_should_confirm_deposit_if_timestamp_is_greater_than_0_and_required_number_of_confirmations_is_achieved()
        {
            const int latestBlockNumber = 3;
            const uint confirmationTimestamp = 1;
            var block = GetBlock();
            var parentBlock = GetBlock();
            var deposit = GetDepositDetails();
            var transaction = GetTransaction();
            _blockchainBridge.GetTransactionAsync(deposit.Transaction.Hash).Returns(transaction);
            _blockchainBridge.GetLatestBlockNumberAsync().Returns(latestBlockNumber);
            _blockchainBridge.FindBlockAsync(latestBlockNumber).Returns(block);
            _blockchainBridge.FindBlockAsync(block.ParentHash).Returns(parentBlock);
            _depositService.VerifyDepositAsync(deposit.Consumer, deposit.Id, block.Header.Number)
                .Returns(confirmationTimestamp);
            await _depositConfirmationService.TryConfirmAsync(deposit);
            deposit.Confirmed.Should().BeTrue();
            deposit.Confirmations.Should().Be(_requiredBlockConfirmations);
            await _depositRepository.Received().UpdateAsync(deposit);
            await _blockchainBridge.Received(2).GetLatestBlockNumberAsync();
            await _consumerNotifier.Received().SendDepositConfirmationsStatusAsync(deposit.Id, deposit.DataAsset.Name,
                _requiredBlockConfirmations, _requiredBlockConfirmations, deposit.ConfirmationTimestamp, true);
        }
        
        [Test]
        public async Task try_confirm_should_reject_deposit_if_transaction_is_missing_block_confirmation()
        {
            const int latestBlockNumber = 3;
            const uint confirmationTimestamp = 1;
            var block = GetBlock();
            var deposit = GetDepositDetails();
            var transaction = GetTransaction();
            block.Header.Hash = transaction.BlockHash;
            _blockchainBridge.GetTransactionAsync(deposit.Transaction.Hash).Returns(transaction);
            _blockchainBridge.GetLatestBlockNumberAsync().Returns(latestBlockNumber);
            _blockchainBridge.FindBlockAsync(latestBlockNumber).Returns(block);
            _depositService.VerifyDepositAsync(deposit.Consumer, deposit.Id, block.Header.Number)
                .Returns(confirmationTimestamp);
            await _depositConfirmationService.TryConfirmAsync(deposit);
            deposit.Rejected.Should().BeTrue();
            await _depositRepository.Received().UpdateAsync(deposit);
            await _consumerNotifier.Received().SendDepositRejectedAsync(deposit.Id);
        }

        private static Block GetBlock()
        {
            var block = Build.A.Block.TestObject;
            block.Header.Number = 2;

            return block;
        }

        private static NdmTransaction GetTransaction(bool pending = false)
            => new NdmTransaction(Build.A.Transaction.TestObject, pending, 1, TestItem.KeccakA, 1);

        private static DepositDetails GetDepositDetails(uint timestamp = 0,
            IEnumerable<TransactionInfo> transactions = null)
            => new DepositDetails(new Deposit(Keccak.Zero, 1, 1, 1),
                GetDataAsset(DataAssetUnitType.Unit), TestItem.AddressB, Array.Empty<byte>(), 1,
                transactions ?? new[] {TransactionInfo.Default(TestItem.KeccakA, 1, 1, 1, 1)}, timestamp);

        private static DataAsset GetDataAsset(DataAssetUnitType unitType)
            => new DataAsset(Keccak.OfAnEmptyString, "test", "test", 1,
                unitType, 0, 10, new DataAssetRules(new DataAssetRule(1)),
                new DataAssetProvider(Address.Zero, "test"));
    }
}