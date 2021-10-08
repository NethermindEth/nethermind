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
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Consumers.Shared.Services;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Shared
{
    public class ConsumerTransactionsServiceTests
    {
        private ITransactionService _transactionService;
        private IDepositDetailsRepository _depositRepository;
        private ITimestamper _timestamper;
        private IConsumerTransactionsService _consumerTransactionsService;

        [SetUp]
        public void Setup()
        {
            _transactionService = Substitute.For<ITransactionService>();
            _depositRepository = Substitute.For<IDepositDetailsRepository>();
            _timestamper = new Timestamper(DateTime.UtcNow);
            _consumerTransactionsService = new ConsumerTransactionsService(_transactionService, _depositRepository,
                _timestamper, LimboLogs.Instance);
        }

        [Test]
        public async Task get_pending_should_return_transactions_with_valid_type()
        {
            var deposits = new[]
            {
                GetDepositDetails(TestItem.KeccakA),
                GetDepositDetails(TestItem.KeccakB, TestItem.KeccakB)
            };
            _depositRepository.BrowseAsync(Arg.Is<GetDeposits>(x => x.OnlyPending && x.Page == 1 &&
                                                                    x.Results == int.MaxValue))
                .Returns(PagedResult<DepositDetails>.Create(deposits, 1, 1, 1, 1));

            var result = await _consumerTransactionsService.GetPendingAsync();

            var transactions = result.ToList();
            transactions.Count.Should().Be(2);
            var deposit = deposits[0];
            var depositWithRefund = deposits[1];
            var depositPendingTransaction = transactions.ElementAt(0);
            var refundPendingTransaction = transactions.ElementAt(1);
            depositPendingTransaction.Transaction.Hash.Should().Be(deposit.Transaction.Hash);
            depositPendingTransaction.Transaction.Value.Should().Be(deposit.Transaction.Value);
            depositPendingTransaction.Transaction.GasPrice.Should().Be(deposit.Transaction.GasPrice);
            depositPendingTransaction.Transaction.Timestamp.Should().Be(deposit.Transaction.Timestamp);
            depositPendingTransaction.Type.Should().Be("deposit");
            refundPendingTransaction.Transaction.Hash.Should().Be(depositWithRefund.ClaimedRefundTransaction.Hash);
            refundPendingTransaction.Transaction.Value.Should().Be(depositWithRefund.ClaimedRefundTransaction.Value);
            refundPendingTransaction.Transaction.Hash.Should().Be(depositWithRefund.ClaimedRefundTransaction.Hash);
            refundPendingTransaction.Transaction.Timestamp.Should().Be(depositWithRefund.ClaimedRefundTransaction.Timestamp);
            refundPendingTransaction.Type.Should().Be("refund");
            await _depositRepository.Received().BrowseAsync(Arg.Is<GetDeposits>(x => x.OnlyPending && x.Page == 1 &&
                                                                                     x.Results == int.MaxValue));
        }

        [Test]
        public async Task update_deposit_gas_price_should_fail_for_zero_gas_price()
        {
            var depositId = TestItem.KeccakA;
            var info = await _consumerTransactionsService.UpdateDepositGasPriceAsync(depositId, 0);
            info.Status.Should().Be(UpdatedTransactionStatus.InvalidGasPrice);
            info.Hash.Should().BeNull();
        }

        [Test]
        public async Task update_deposit_gas_price_should_fail_for_not_existing_deposit()
        {
            var depositId = TestItem.KeccakA;
            var info = await _consumerTransactionsService.UpdateDepositGasPriceAsync(depositId, 1);
            info.Status.Should().Be(UpdatedTransactionStatus.ResourceNotFound);
            info.Hash.Should().BeNull();
        }

        [Test]
        public async Task update_deposit_gas_price_should_fail_for_deposit_with_empty_transaction_hash()
        {
            var deposit = GetDepositDetails();
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            var info = await _consumerTransactionsService.UpdateDepositGasPriceAsync(deposit.Id, 1);
            info.Status.Should().Be(UpdatedTransactionStatus.MissingTransaction);
            info.Hash.Should().BeNull();
            await _depositRepository.Received().GetAsync(deposit.Id);
        }

        [Test]
        public async Task update_deposit_gas_price_should_fail_for_rejected_deposit()
        {
            var transactionHash = TestItem.KeccakC;
            var deposit = GetDepositDetails(transactionHash);
            deposit.Reject();
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            var info = await _consumerTransactionsService.UpdateDepositGasPriceAsync(deposit.Id, 1);
            info.Status.Should().Be(UpdatedTransactionStatus.ResourceRejected);
            info.Hash.Should().BeNull();
            await _depositRepository.Received().GetAsync(deposit.Id);
        }

        [Test]
        public async Task update_deposit_gas_price_should_fail_for_confirmed_deposit()
        {
            var transactionHash = TestItem.KeccakC;
            var deposit = GetDepositDetails(transactionHash);
            deposit.SetConfirmations(1);
            deposit.SetConfirmationTimestamp(1);
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            var info = await _consumerTransactionsService.UpdateDepositGasPriceAsync(deposit.Id, 1);
            info.Status.Should().Be(UpdatedTransactionStatus.ResourceConfirmed);
            info.Hash.Should().BeNull();
            await _depositRepository.Received().GetAsync(deposit.Id);
        }

        [Test]
        public async Task update_deposit_gas_price_should_return_transaction_status()
        {
            var transactionHash = TestItem.KeccakC;
            var newTransactionHash = TestItem.KeccakD;
            var gasPrice = 10.GWei();
            var deposit = GetDepositDetails(transactionHash);
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            _transactionService.UpdateGasPriceAsync(deposit.Transaction.Hash, gasPrice).Returns(newTransactionHash);
            var info = await _consumerTransactionsService.UpdateDepositGasPriceAsync(deposit.Id, gasPrice);
            info.Hash.Should().Be(newTransactionHash);
            info.Status.Should().Be(UpdatedTransactionStatus.Ok);
            deposit.Transaction.Hash.Should().Be(newTransactionHash);
            deposit.Transaction.GasPrice.Should().Be(gasPrice);
            await _depositRepository.Received().GetAsync(deposit.Id);
            await _depositRepository.Received().UpdateAsync(deposit);
            await _transactionService.Received().UpdateGasPriceAsync(transactionHash, gasPrice);
        }

        [Test]
        public async Task update_refund_gas_price_should_fail_for_zero_gas_price()
        {
            var depositId = TestItem.KeccakA;
            var info = await _consumerTransactionsService.UpdateRefundGasPriceAsync(depositId, 0);
            info.Status.Should().Be(UpdatedTransactionStatus.InvalidGasPrice);
            info.Hash.Should().BeNull();
        }

        [Test]
        public async Task update_refund_gas_price_should_fail_for_not_existing_deposit()
        {
            var depositId = TestItem.KeccakA;
            var info = await _consumerTransactionsService.UpdateRefundGasPriceAsync(depositId, 1);
            info.Status.Should().Be(UpdatedTransactionStatus.ResourceNotFound);
            info.Hash.Should().BeNull();
        }

        [Test]
        public async Task update_refund_gas_price_should_fail_for_deposit_with_empty_transaction_hash()
        {
            var deposit = GetDepositDetails();
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            var info = await _consumerTransactionsService.UpdateRefundGasPriceAsync(deposit.Id, 1);
            info.Status.Should().Be(UpdatedTransactionStatus.MissingTransaction);
            info.Hash.Should().BeNull();
        }
        
        [Test]
        public async Task update_refund_gas_price_should_fail_for_cancelled_deposit()
        {
            var transactionHash = TestItem.KeccakA;
            var deposit = GetDepositDetails(transactionHash, cancelled: true);
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            var info = await _consumerTransactionsService.UpdateRefundGasPriceAsync(deposit.Id, 1);
            info.Status.Should().Be(UpdatedTransactionStatus.ResourceCancelled);
            info.Hash.Should().BeNull();
            await _depositRepository.Received().GetAsync(deposit.Id);
        }

        [Test]
        public async Task update_refund_gas_price_should_fail_for_rejected_deposit()
        {
            var transactionHash = TestItem.KeccakC;
            var deposit = GetDepositDetails(transactionHash);
            deposit.Reject();
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            var info = await _consumerTransactionsService.UpdateRefundGasPriceAsync(deposit.Id, 1);
            info.Status.Should().Be(UpdatedTransactionStatus.ResourceRejected);
            info.Hash.Should().BeNull();
            await _depositRepository.Received().GetAsync(deposit.Id);
        }

        [Test]
        public async Task update_refund_gas_price_should_fail_for_deposit_with_empty_claimed_refund_transaction_hash()
        {
            var transactionHash = TestItem.KeccakC;
            var deposit = GetDepositDetails(transactionHash);
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            var info = await _consumerTransactionsService.UpdateRefundGasPriceAsync(deposit.Id, 1);
            info.Status.Should().Be(UpdatedTransactionStatus.MissingTransaction);
            info.Hash.Should().BeNull();
            await _depositRepository.Received().GetAsync(deposit.Id);
        }

        [Test]
        public async Task update_refund_gas_price_should_fail_for_claimed_refund()
        {
            var transactionHash = TestItem.KeccakC;
            var claimedRefundTransactionHash = TestItem.KeccakD;
            var deposit = GetDepositDetails(transactionHash, claimedRefundTransactionHash);
            deposit.SetRefundClaimed();
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            var info = await _consumerTransactionsService.UpdateRefundGasPriceAsync(deposit.Id, 1);
            info.Status.Should().Be(UpdatedTransactionStatus.ResourceConfirmed);
            info.Hash.Should().BeNull();
            await _depositRepository.Received().GetAsync(deposit.Id);
        }

        [Test]
        public async Task update_refund_gas_price_should_return_transaction_status()
        {
            var transactionHash = TestItem.KeccakC;
            var claimedRefundTransactionHash = TestItem.KeccakD;
            var newTransactionHash = TestItem.KeccakE;
            var gasPrice = 10.GWei();
            var deposit = GetDepositDetails(transactionHash, claimedRefundTransactionHash);
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            _transactionService.UpdateGasPriceAsync(deposit.ClaimedRefundTransaction.Hash, gasPrice)
                .Returns(newTransactionHash);
            var info = await _consumerTransactionsService.UpdateRefundGasPriceAsync(deposit.Id, gasPrice);
            info.Hash.Should().Be(newTransactionHash);
            info.Status.Should().Be(UpdatedTransactionStatus.Ok);
            deposit.ClaimedRefundTransaction.Hash.Should().Be(newTransactionHash);
            deposit.ClaimedRefundTransaction.GasPrice.Should().Be(gasPrice);
            deposit.ClaimedRefundTransaction.Type.Should().Be(TransactionType.SpeedUp);
            await _depositRepository.Received().GetAsync(deposit.Id);
            await _depositRepository.Received().UpdateAsync(deposit);
            await _transactionService.Received().UpdateGasPriceAsync(claimedRefundTransactionHash, gasPrice);
        }
        
        [Test]
        public async Task cancel_deposit_should_fail_for_missing_deposit()
        {
            var depositId = TestItem.KeccakA;
            var info = await _consumerTransactionsService.CancelDepositAsync(depositId);
            info.Status.Should().Be(UpdatedTransactionStatus.ResourceNotFound);
            info.Hash.Should().BeNull();
            await _depositRepository.Received().GetAsync(depositId);
        }
        
        [Test]
        public async Task cancel_deposit_should_fail_for_confirmed_deposit()
        {
            var transactionHash = TestItem.KeccakA;
            var deposit = GetDepositDetails(transactionHash);
            deposit.SetConfirmations(1);
            deposit.SetConfirmationTimestamp(1);
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            var info = await _consumerTransactionsService.CancelDepositAsync(deposit.Id);
            info.Status.Should().Be(UpdatedTransactionStatus.ResourceConfirmed);
            info.Hash.Should().BeNull();
            await _depositRepository.Received().GetAsync(deposit.Id);
        }
        
        [Test]
        public async Task cancel_deposit_should_fail_for_not_pending_transaction()
        {
            var transactionHash = TestItem.KeccakA;
            var deposit = GetDepositDetails(transactionHash);
            deposit.Transaction.SetIncluded();
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            var info = await _consumerTransactionsService.CancelDepositAsync(deposit.Id);
            info.Status.Should().Be(UpdatedTransactionStatus.AlreadyIncluded);
            info.Hash.Should().BeNull();
            await _depositRepository.Received().GetAsync(deposit.Id);
        }
        
        [Test]
        public async Task cancel_refund_should_fail_for_missing_deposit()
        {
            var depositId = TestItem.KeccakA;
            var info = await _consumerTransactionsService.CancelRefundAsync(depositId);
            info.Status.Should().Be(UpdatedTransactionStatus.ResourceNotFound);
            info.Hash.Should().BeNull();
            await _depositRepository.Received().GetAsync(depositId);
        }
        
        [Test]
        public async Task cancel_refund_should_fail_for_missing_transaction()
        {
            var transactionHash = TestItem.KeccakA;
            var deposit = GetDepositDetails(transactionHash);
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            var info = await _consumerTransactionsService.CancelRefundAsync(deposit.Id);
            info.Status.Should().Be(UpdatedTransactionStatus.MissingTransaction);
            info.Hash.Should().BeNull();
            await _depositRepository.Received().GetAsync(deposit.Id);
        }
        
        [Test]
        public async Task cancel_refund_should_fail_if_already_claimed()
        {
            var transactionHash = TestItem.KeccakA;
            var deposit = GetDepositDetails(transactionHash);
            deposit.AddClaimedRefundTransaction(TransactionInfo.Default(TestItem.KeccakB, 0, 0, 0, 0));
            deposit.SetRefundClaimed();
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            var info = await _consumerTransactionsService.CancelRefundAsync(deposit.Id);
            info.Status.Should().Be(UpdatedTransactionStatus.ResourceConfirmed);
            info.Hash.Should().BeNull();
            await _depositRepository.Received().GetAsync(deposit.Id);
        }
        
        [Test]
        public async Task cancel_refund_should_fail_for_cancelled_deposit()
        {
            var transactionHash = TestItem.KeccakA;
            var deposit = GetDepositDetails(transactionHash, cancelled: true);
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            var info = await _consumerTransactionsService.CancelRefundAsync(deposit.Id);
            info.Status.Should().Be(UpdatedTransactionStatus.ResourceCancelled);
            info.Hash.Should().BeNull();
            await _depositRepository.Received().GetAsync(deposit.Id);
        }
        
        [Test]
        public async Task cancel_refund_should_fail_for_rejected_deposit()
        {
            var transactionHash = TestItem.KeccakA;
            var deposit = GetDepositDetails(transactionHash);
            deposit.Reject();
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            var info = await _consumerTransactionsService.CancelRefundAsync(deposit.Id);
            info.Status.Should().Be(UpdatedTransactionStatus.ResourceRejected);
            info.Hash.Should().BeNull();
            await _depositRepository.Received().GetAsync(deposit.Id);
        }

        [Test]
        public async Task cancel_refund_should_fail_for_not_pending_transaction()
        {
            var transactionHash = TestItem.KeccakA;
            var deposit = GetDepositDetails(transactionHash);
            deposit.AddClaimedRefundTransaction(new TransactionInfo(TestItem.KeccakB, 0, 0, 0, 0,
                state: TransactionState.Included));
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            var info = await _consumerTransactionsService.CancelRefundAsync(deposit.Id);
            info.Status.Should().Be(UpdatedTransactionStatus.AlreadyIncluded);
            info.Hash.Should().BeNull();
            await _depositRepository.Received().GetAsync(deposit.Id);
        }

        [Test]
        public async Task cancel_deposit_should_return_transaction_status()
        {
            var transactionHash = TestItem.KeccakA;
            var deposit = GetDepositDetails(transactionHash);
            var info = new CanceledTransactionInfo(TestItem.KeccakB, 10.GWei(), 1000);
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            _transactionService.CancelAsync(transactionHash).Returns(info);
            var transactionInfo = await _consumerTransactionsService.CancelDepositAsync(deposit.Id);
            transactionInfo.Hash.Should().Be(info.Hash);
            transactionInfo.Status.Should().Be(UpdatedTransactionStatus.Ok);
            deposit.Transaction.Hash.Should().Be(transactionInfo.Hash);
            deposit.Transaction.Type.Should().Be(TransactionType.Cancellation);
            await _depositRepository.Received().GetAsync(deposit.Id);
            await _depositRepository.Received().UpdateAsync(deposit);
            await _transactionService.Received().CancelAsync(transactionHash);
        }
        
        [Test]
        public async Task cancel_refund_should_return_transaction_status()
        {
            var transactionHash = TestItem.KeccakA;
            var claimedRefundTransactionHash = TestItem.KeccakB;
            var deposit = GetDepositDetails(transactionHash, claimedRefundTransactionHash);
            var info = new CanceledTransactionInfo(TestItem.KeccakB, 10.GWei(), 1000);
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            _transactionService.CancelAsync(claimedRefundTransactionHash).Returns(info);
            var transactionInfo = await _consumerTransactionsService.CancelRefundAsync(deposit.Id);
            transactionInfo.Hash.Should().Be(info.Hash);
            transactionInfo.Status.Should().Be(UpdatedTransactionStatus.Ok);
            deposit.ClaimedRefundTransaction.Hash.Should().Be(transactionInfo.Hash);
            deposit.ClaimedRefundTransaction.Type.Should().Be(TransactionType.Cancellation);
            await _depositRepository.Received().GetAsync(deposit.Id);
            await _depositRepository.Received().UpdateAsync(deposit);
            await _transactionService.Received().CancelAsync(claimedRefundTransactionHash);
        }

        private static DepositDetails GetDepositDetails(Keccak transactionHash = null,
            Keccak claimedRefundTransactionHash = null, bool cancelled = false)
            => new DepositDetails(new Deposit(TestItem.KeccakA, 1, 1, 1),
                GetDataAsset(DataAssetUnitType.Unit), TestItem.AddressB, Array.Empty<byte>(), 1,
                transactionHash is null ? Array.Empty<TransactionInfo>() : new[] {TransactionInfo.Default(transactionHash, 1, 1, 1, 1)},
                claimedRefundTransactions: claimedRefundTransactionHash is null
                    ? Array.Empty<TransactionInfo>()
                    : new[] {TransactionInfo.Default(claimedRefundTransactionHash, 1, 1, 1, 1)},
                cancelled: cancelled);

        private static DataAsset GetDataAsset(DataAssetUnitType unitType)
            => new DataAsset(Keccak.OfAnEmptyString, "test", "test", 1,
                unitType, 0, 10, new DataAssetRules(new DataAssetRule(1)),
                new DataAssetProvider(Address.Zero, "test"));
    }
}