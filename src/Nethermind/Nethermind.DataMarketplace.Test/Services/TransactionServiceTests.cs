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
using System.Numerics;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Services
{
    public class TransactionServiceTests
    {
        private const string ConfigId = "ndm";
        private INdmBlockchainBridge _blockchainBridge;
        private IWallet _wallet;
        private IConfigManager _configManager;
        private NdmConfig _config;
        private ITransactionService _transactionService;

        [SetUp]
        public void Setup()
        {
            _blockchainBridge = Substitute.For<INdmBlockchainBridge>();
            _wallet = Substitute.For<IWallet>();
            _wallet.Sign(Arg.Any<Keccak>(), Arg.Any<Address>()).Returns(new Signature(new byte[65]));
            _configManager = Substitute.For<IConfigManager>();
            _config = new NdmConfig();
            _configManager.GetAsync(ConfigId).Returns(_config);
            _transactionService = new TransactionService(_blockchainBridge, _wallet, _configManager, ConfigId, LimboLogs.Instance);
        }

        [Test]
        public void update_gas_price_should_fail_if_transaction_hash_is_null()
        {
            Func<Task> act = () => _transactionService.UpdateGasPriceAsync(null, 1);
            act.Should().Throw<ArgumentException>()
                .WithMessage("Transaction hash cannot be null. (Parameter 'transactionHash')");
        }
        
        [Test]
        public void update_gas_price_should_fail_if_price_is_0()
        {
            Func<Task> act = () => _transactionService.UpdateGasPriceAsync(TestItem.KeccakA, 0);
            act.Should().Throw<ArgumentException>()
                .WithMessage("Gas price cannot be 0. (Parameter 'gasPrice')");
        }
        
        [Test]
        public void update_gas_price_should_fail_if_transaction_was_not_found()
        {
            var transactionHash = TestItem.KeccakA;
            Func<Task> act = () => _transactionService.UpdateGasPriceAsync(transactionHash, 1);
            act.Should().Throw<ArgumentException>()
                .WithMessage($"Transaction was not found for hash: '{transactionHash}'. (Parameter 'transactionHash')");
            _blockchainBridge.Received().GetTransactionAsync(transactionHash);
        }

        [Test]
        public void update_gas_price_should_fail_if_transaction_is_not_pending()
        {
            var transactionHash = TestItem.KeccakA;
            var transaction = Build.A.Transaction.TestObject;
            _blockchainBridge.GetTransactionAsync(transactionHash)
                .Returns(new NdmTransaction(transaction, false, 1, TestItem.KeccakB, 1));
            Func<Task> act = () => _transactionService.UpdateGasPriceAsync(transactionHash, 1);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage($"Transaction with hash: '{transactionHash}' is not pending.");
            _blockchainBridge.Received().GetTransactionAsync(transactionHash);
        }

        [Test]
        public void update_gas_price_should_fail_if_sending_new_transaction_fails()
        {
            var transactionHash = TestItem.KeccakA;
            var transaction = Build.A.Transaction.TestObject;
            _blockchainBridge.GetTransactionAsync(transactionHash)
                .Returns(new NdmTransaction(transaction, true, 1, TestItem.KeccakB, 1));
            Func<Task> act = () => _transactionService.UpdateGasPriceAsync(transactionHash, 1);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("Transaction was not sent (received an empty hash).");
            _blockchainBridge.Received().GetTransactionAsync(transactionHash);
            _blockchainBridge.Received().GetNetworkIdAsync();
            _blockchainBridge.Received().SendOwnTransactionAsync(transaction);
        }

        [Test]
        public async Task update_gas_price_should_succeed_if_sending_new_transaction_succeeds()
        {
            var transactionHash = TestItem.KeccakA;
            var transaction = Build.A.Transaction.TestObject;
            var sentTransactionHash = TestItem.KeccakB;
            var gasPrice = 30.GWei();
            _blockchainBridge.GetTransactionAsync(transactionHash)
                .Returns(new NdmTransaction(transaction, true, 1, TestItem.KeccakB, 1));
            _blockchainBridge.SendOwnTransactionAsync(transaction).Returns(sentTransactionHash);
            var hash = await _transactionService.UpdateGasPriceAsync(transactionHash, gasPrice);
            hash.Should().Be(sentTransactionHash);
            transaction.GasPrice.Should().Be(gasPrice);
            await _blockchainBridge.Received().GetTransactionAsync(transactionHash);
            await _blockchainBridge.Received().GetNetworkIdAsync();
            await _blockchainBridge.Received().SendOwnTransactionAsync(transaction);
        }
        
        [Test]
        public async Task update_value_should_succeed_if_sending_new_transaction_succeeds()
        {
            var transactionHash = TestItem.KeccakA;
            var transaction = Build.A.Transaction.TestObject;
            var sentTransactionHash = TestItem.KeccakB;
            var value = 10.GWei();
            _blockchainBridge.GetTransactionAsync(transactionHash)
                .Returns(new NdmTransaction(transaction, true, 1, TestItem.KeccakB, 1));
            _blockchainBridge.SendOwnTransactionAsync(transaction).Returns(sentTransactionHash);
            var hash = await _transactionService.UpdateValueAsync(transactionHash, value);
            hash.Should().Be(sentTransactionHash);
            transaction.Value.Should().Be(value);
            await _blockchainBridge.Received().GetTransactionAsync(transactionHash);
            await _blockchainBridge.Received().GetNetworkIdAsync();
            await _blockchainBridge.Received().SendOwnTransactionAsync(transaction);
        }

        [Test]
        public void cancel_should_fail_if_gas_price_multiplier_is_0()
        {
            var transactionHash = TestItem.KeccakA;
            _config.CancelTransactionGasPricePercentageMultiplier = 0;
            Func<Task> act = () => _transactionService.CancelAsync(transactionHash);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("Multiplier for gas price when canceling transaction cannot be 0.");
            _blockchainBridge.DidNotReceive().GetTransactionAsync(transactionHash);
        }

        [Test]
        public void  cancel_should_fail_if_transaction_is_not_pending()
        {
            var transactionHash = TestItem.KeccakA;
            var transaction = Build.A.Transaction.TestObject;
            _blockchainBridge.GetTransactionAsync(transactionHash)
                .Returns(new NdmTransaction(transaction, false, 1, TestItem.KeccakB, 1));
            Func<Task> act = () => _transactionService.CancelAsync(transactionHash);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage($"Transaction with hash: '{transactionHash}' is not pending.");
            _blockchainBridge.Received().GetTransactionAsync(transactionHash);
        }

        [Test]
        public async Task cancel_should_succeed_if_sending_new_transaction_succeeds()
        {
            var gasPrice = 10.GWei();
            var transactionHash = TestItem.KeccakA;
            var transaction = Build.A.Transaction.WithGasPrice(gasPrice).TestObject;
            var sentTransactionHash = TestItem.KeccakB;
            _blockchainBridge.GetTransactionAsync(transactionHash)
                .Returns(new NdmTransaction(transaction, true, 1, TestItem.KeccakB, 1));
            _blockchainBridge.SendOwnTransactionAsync(transaction).Returns(sentTransactionHash);
            var transactionInfo = await _transactionService.CancelAsync(transactionHash);
            transactionInfo.Hash.Should().Be(sentTransactionHash);
            transaction.GasLimit.Should().Be(21000);
            transaction.Value.Should().Be(0);
            transaction.GasLimit.Should().Be(21000);
            var expectedGasPrice = _config.CancelTransactionGasPricePercentageMultiplier * gasPrice / 100;
            transaction.GasPrice.Should().Be(expectedGasPrice);
            await _blockchainBridge.Received().GetTransactionAsync(transactionHash);
            await _blockchainBridge.Received().GetNetworkIdAsync();
            await _blockchainBridge.Received().SendOwnTransactionAsync(transaction);
        }
    }
}
