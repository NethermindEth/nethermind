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
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Shared
{
    public class ConsumerTransactionsServiceTests
    {
        private ITransactionService _transactionService;
        private IDepositDetailsRepository _depositRepository;
        private IConsumerTransactionsService _consumerTransactionsService;

        [SetUp]
        public void Setup()
        {
            _transactionService = Substitute.For<ITransactionService>();
            _depositRepository = Substitute.For<IDepositDetailsRepository>();
            _consumerTransactionsService = new ConsumerTransactionsService(_transactionService, _depositRepository,
                LimboLogs.Instance);
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
            depositPendingTransaction.Hash.Should().Be(deposit.TransactionHash);
            depositPendingTransaction.GasPrice.Should().Be(deposit.TransactionGasPrice);
            depositPendingTransaction.Type.Should().Be("deposit");
            refundPendingTransaction.Hash.Should().Be(depositWithRefund.ClaimedRefundTransactionHash);
            refundPendingTransaction.GasPrice.Should().Be(depositWithRefund.ClaimedRefundTransactionGasPrice);
            refundPendingTransaction.Type.Should().Be("refund");
            await _depositRepository.Received().BrowseAsync(Arg.Is<GetDeposits>(x => x.OnlyPending && x.Page == 1 &&
                                                                                     x.Results == int.MaxValue));
        }

        [Test]
        public void update_deposit_gas_price_should_fail_for_zero_gas_price()
        {
            var depositId = TestItem.KeccakA;
            Func<Task> act = () => _consumerTransactionsService.UpdateDepositGasPriceAsync(depositId, 0);
            act.Should().Throw<ArgumentException>()
                .WithMessage("Gas price cannot be 0. (Parameter 'gasPrice')");
        }

        [Test]
        public void update_deposit_gas_price_should_fail_for_not_existing_deposit()
        {
            var depositId = TestItem.KeccakA;
            Func<Task> act = () => _consumerTransactionsService.UpdateDepositGasPriceAsync(depositId, 1);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage($"Deposit with id: '{depositId}' was not found.");
        }

        [Test]
        public async Task update_deposit_gas_price_should_fail_for_deposit_with_empty_transaction_hash()
        {
            var deposit = GetDepositDetails();
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            Func<Task> act = () => _consumerTransactionsService.UpdateDepositGasPriceAsync(deposit.Id, 1);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage($"Deposit with id: '{deposit.Id}' has no transaction hash.");
            await _depositRepository.Received().GetAsync(deposit.Id);
        }

        [Test]
        public async Task update_deposit_gas_price_should_fail_for_rejected_deposit()
        {
            var transactionHash = TestItem.KeccakC;
            var deposit = GetDepositDetails(transactionHash);
            deposit.Reject();
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            Func<Task> act = () => _consumerTransactionsService.UpdateDepositGasPriceAsync(deposit.Id, 1);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage($"Deposit with id: '{deposit.Id}' was rejected.");
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
            Func<Task> act = () => _consumerTransactionsService.UpdateDepositGasPriceAsync(deposit.Id, 1);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage($"Deposit with id: '{deposit.Id}' was confirmed.");
            await _depositRepository.Received().GetAsync(deposit.Id);
        }

        [Test]
        public async Task update_deposit_gas_price_should_succeed()
        {
            var transactionHash = TestItem.KeccakC;
            var newTransactionHash = TestItem.KeccakD;
            var gasPrice = 10.GWei();
            var deposit = GetDepositDetails(transactionHash);
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            _transactionService.UpdateGasPriceAsync(deposit.TransactionHash, gasPrice).Returns(newTransactionHash);
            var hash = await _consumerTransactionsService.UpdateDepositGasPriceAsync(deposit.Id, gasPrice);
            hash.Should().Be(newTransactionHash);
            deposit.TransactionHash.Should().Be(newTransactionHash);
            deposit.TransactionGasPrice.Should().Be(gasPrice);
            await _depositRepository.Received().GetAsync(deposit.Id);
            await _transactionService.Received().UpdateGasPriceAsync(transactionHash, gasPrice);
        }

        [Test]
        public void update_refund_gas_price_should_fail_for_zero_gas_price()
        {
            var depositId = TestItem.KeccakA;
            Func<Task> act = () => _consumerTransactionsService.UpdateRefundGasPriceAsync(depositId, 0);
            act.Should().Throw<ArgumentException>()
                .WithMessage("Gas price cannot be 0. (Parameter 'gasPrice')");
        }

        [Test]
        public void update_refund_gas_price_should_fail_for_not_existing_deposit()
        {
            var depositId = TestItem.KeccakA;
            Func<Task> act = () => _consumerTransactionsService.UpdateRefundGasPriceAsync(depositId, 1);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage($"Deposit with id: '{depositId}' was not found.");
        }

        [Test]
        public async Task update_refund_gas_price_should_fail_for_deposit_with_empty_transaction_hash()
        {
            var deposit = GetDepositDetails();
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            Func<Task> act = () => _consumerTransactionsService.UpdateRefundGasPriceAsync(deposit.Id, 1);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage($"Deposit with id: '{deposit.Id}' has no transaction hash.");
            await _depositRepository.Received().GetAsync(deposit.Id);
        }

        [Test]
        public async Task update_refund_gas_price_should_fail_for_rejected_deposit()
        {
            var transactionHash = TestItem.KeccakC;
            var deposit = GetDepositDetails(transactionHash);
            deposit.Reject();
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            Func<Task> act = () => _consumerTransactionsService.UpdateRefundGasPriceAsync(deposit.Id, 1);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage($"Deposit with id: '{deposit.Id}' was rejected.");
            await _depositRepository.Received().GetAsync(deposit.Id);
        }

        [Test]
        public async Task update_refund_gas_price_should_fail_for_deposit_with_empty_claimed_refund_transaction_hash()
        {
            var transactionHash = TestItem.KeccakC;
            var deposit = GetDepositDetails(transactionHash);
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            Func<Task> act = () => _consumerTransactionsService.UpdateRefundGasPriceAsync(deposit.Id, 1);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage($"Deposit with id: '{deposit.Id}' has no transaction hash for refund claim.");
            await _depositRepository.Received().GetAsync(deposit.Id);
        }

        [Test]
        public async Task update_refund_gas_price_should_fail_for_claimed_refund()
        {
            var transactionHash = TestItem.KeccakC;
            var claimedRefundTransactionHash = TestItem.KeccakD;
            var deposit =
                GetDepositDetails(transactionHash, claimedRefundTransactionHash: claimedRefundTransactionHash);
            deposit.SetRefundClaimed();
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            Func<Task> act = () => _consumerTransactionsService.UpdateRefundGasPriceAsync(deposit.Id, 1);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage(
                    $"Deposit with id: '{deposit.Id}' has already claimed refund (transaction hash: '{deposit.ClaimedRefundTransactionHash}').");
            await _depositRepository.Received().GetAsync(deposit.Id);
        }

        [Test]
        public async Task update_refund_gas_price_should_succeed()
        {
            var transactionHash = TestItem.KeccakC;
            var claimedRefundTransactionHash = TestItem.KeccakD;
            var newTransactionHash = TestItem.KeccakE;
            var gasPrice = 10.GWei();
            var deposit = GetDepositDetails(transactionHash, claimedRefundTransactionHash);
            _depositRepository.GetAsync(deposit.Id).Returns(deposit);
            _transactionService.UpdateGasPriceAsync(deposit.ClaimedRefundTransactionHash, gasPrice)
                .Returns(newTransactionHash);
            var hash = await _consumerTransactionsService.UpdateRefundGasPriceAsync(deposit.Id, gasPrice);
            hash.Should().Be(newTransactionHash);
            deposit.ClaimedRefundTransactionHash.Should().Be(newTransactionHash);
            deposit.ClaimedRefundTransactionGasPrice.Should().Be(gasPrice);
            await _depositRepository.Received().GetAsync(deposit.Id);
            await _transactionService.Received().UpdateGasPriceAsync(claimedRefundTransactionHash, gasPrice);
        }

        [Test]
        public void cancel_should_fail_for_empty_transaction_hash()
        {
            Func<Task> act = () => _consumerTransactionsService.CancelAsync(null);
            act.Should().Throw<ArgumentException>()
                .WithMessage("Transaction hash cannot be empty. (Parameter 'transactionHash')");
        }

        [Test]
        public async Task cancel_should_return_transaction_hash()
        {
            var transactionHash = TestItem.KeccakA;
            var canceledTransactionHash = TestItem.KeccakB;
            _transactionService.CancelAsync(transactionHash).Returns(canceledTransactionHash);
            var hash = await _consumerTransactionsService.CancelAsync(transactionHash);
            hash.Should().Be(canceledTransactionHash);
            await _transactionService.Received().CancelAsync(transactionHash);
        }

        private static DepositDetails GetDepositDetails(Keccak transactionHash = null,
            Keccak claimedRefundTransactionHash = null)
            => new DepositDetails(new Deposit(TestItem.KeccakA, 1, 1, 1),
                GetDataAsset(DataAssetUnitType.Unit), TestItem.AddressB, Array.Empty<byte>(), 1, transactionHash,
                1, claimedRefundTransactionHash: claimedRefundTransactionHash);

        private static DataAsset GetDataAsset(DataAssetUnitType unitType)
            => new DataAsset(Keccak.OfAnEmptyString, "test", "test", 1,
                unitType, 0, 10, new DataAssetRules(new DataAssetRule(1)),
                new DataAssetProvider(Address.Zero, "test"));
    }
}