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
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Services
{
    public class NdmBlockchainBridgeTests
    {
        private INdmBlockchainBridge _ndmBridge;
        private IBlockchainBridge _blockchainBridge;
        private ITxPool _txPool;

        [SetUp]
        public void Setup()
        {
            _blockchainBridge = Substitute.For<IBlockchainBridge>();
            _txPool = Substitute.For<ITxPool>();
            _ndmBridge = new NdmBlockchainBridge(_blockchainBridge, _txPool);
        }

        [Test]
        public void constructor_should_throw_exception_if_blockchain_bridge_argument_is_null()
        {
            Action act = () => _ndmBridge = new NdmBlockchainBridge(null, _txPool);
            act.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void constructor_should_throw_exception_if_tx_pool_argument_is_null()
        {
            Action act = () => _ndmBridge = new NdmBlockchainBridge(_blockchainBridge, null);
            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public async Task get_latest_block_number_should_return_0_if_head_is_null()
        {
            var result = await _ndmBridge.GetLatestBlockNumberAsync();
            result.Should().Be(0);
        }
        
        [Test]
        public async Task get_latest_block_number_should_return_head_number()
        {
            var header = Build.A.BlockHeader.TestObject;
            _blockchainBridge.Head.Returns(header);
            var result = await _ndmBridge.GetLatestBlockNumberAsync();
            result.Should().Be(_blockchainBridge.Head.Number);
        }
        
        [Test]
        public async Task get_code_should_invoke_blockchain_bridge_get_code()
        {
            var code = new byte[] {0, 1, 2};
            var address = TestItem.AddressA;
            _blockchainBridge.GetCode(address).Returns(code);
            var result = await _ndmBridge.GetCodeAsync(address);
            _blockchainBridge.Received().GetCode(address);
            result.Should().BeSameAs(code);
        }
        
        [Test]
        public async Task find_block_by_hash_should_invoke_blockchain_bridge_find_block_by_hash()
        {
            var block = Build.A.Block.TestObject;
            _blockchainBridge.FindBlock(block.Hash).Returns(block);
            var result = await _ndmBridge.FindBlockAsync(block.Hash);
            _blockchainBridge.Received().FindBlock(block.Hash);
            result.Should().Be(block);
        }
        
        [Test]
        public async Task find_block_by_number_should_invoke_blockchain_bridge_find_block_by_number()
        {
            var block = Build.A.Block.TestObject;
            _blockchainBridge.FindBlock(block.Number).Returns(block);
            var result = await _ndmBridge.FindBlockAsync(block.Number);
            _blockchainBridge.Received().FindBlock(block.Number);
            result.Should().Be(block);
        }
        
        [Test]
        public async Task get_latest_block_number_should_return_null_if_head_is_null()
        {
            var result = await _ndmBridge.GetLatestBlockAsync();
            result.Should().BeNull();
        }
        
        [Test]
        public async Task get_latest_block_should_return_head_number()
        {
            var block = Build.A.Block.TestObject;
            var header = Build.A.BlockHeader.TestObject;
            _blockchainBridge.Head.Returns(header);
            _blockchainBridge.FindBlock(header.Hash).Returns(block);
            var result = await _ndmBridge.GetLatestBlockAsync();
            result.Should().Be(block);
            _blockchainBridge.Received().FindBlock(header.Hash);
        }
        
        [Test]
        public async Task get_nonce_should_invoke_blockchain_bridge_get_nonce()
        {
            UInt256 nonce = 1;
            var address = TestItem.AddressA;
            _blockchainBridge.GetNonce(address).Returns(nonce);
            var result = await _ndmBridge.GetNonceAsync(address);
            _blockchainBridge.Received().GetNonce(address);
            result.Should().Be(nonce);
        }
        
        [Test]
        public async Task reserve_own_transaction_nonce_should_invoke_tx_pool_reserve_own_transaction_nonce()
        {
            UInt256 nonce = 1;
            var address = TestItem.AddressA;
            _txPool.ReserveOwnTransactionNonce(address).Returns(nonce);
            var result = await _ndmBridge.ReserveOwnTransactionNonceAsync(address);
            _txPool.Received().ReserveOwnTransactionNonce(address);
            result.Should().Be(nonce);
        }
        
        [Test]
        public async Task get_transaction_should_return_null_if_receipt_or_transaction_is_null()
        {
            var hash = TestItem.KeccakA;
            var result = await _ndmBridge.GetTransactionAsync(hash);
            result.Should().BeNull();
            _blockchainBridge.Received().GetTransaction(hash);
        }

        [Test]
        public async Task get_transaction_should_invoke_blockchain_bridge_get_transaction_and_return_ndm_transaction()
        {
            var receipt = Build.A.Receipt.TestObject;
            var transaction = Build.A.Transaction.TestObject;
            var tuple = (receipt, transaction);
            var hash = TestItem.KeccakA;
            _blockchainBridge.GetTransaction(hash).Returns(tuple);
            var result = await _ndmBridge.GetTransactionAsync(hash);
            result.Should().NotBeNull();
            _blockchainBridge.Received().GetTransaction(hash);
            result.Transaction.Should().Be(transaction);
            result.BlockNumber.Should().Be(receipt.BlockNumber);
            result.BlockHash.Should().Be(receipt.BlockHash);
            result.GasUsed.Should().Be(receipt.GasUsed);
        }
        
        [Test]
        public async Task get_network_id_should_invoke_blockchain_bridge_get_network_id()
        {
            const int networkId = 1;
            _blockchainBridge.GetNetworkId().Returns(networkId);
            var result = await _ndmBridge.GetNetworkIdAsync();
            _blockchainBridge.Received().GetNetworkId();
            result.Should().Be(networkId);
        }
        
        [Test]
        public async Task call_should_invoke_blockchain_bridge_call_and_return_data()
        {
            var head = Build.A.BlockHeader.TestObject;
            var transaction = Build.A.Transaction.TestObject;
            var data = new byte[] {0, 1, 2};
            _blockchainBridge.Head.Returns(head);
            var output = new BlockchainBridge.CallOutput(data, 0, null);
            _blockchainBridge.Call(head, transaction).Returns(output);
            var result = await _ndmBridge.CallAsync(transaction);
            _blockchainBridge.Received().Call(head, transaction);
            result.Should().BeSameAs(data);
        }
        
        [Test]
        public async Task call_with_transaction_number_for_invalid_block_should_invoke_blockchain_bridge_call_and_return_empty_data()
        {
            const int blockNumber = 1;
            var transaction = Build.A.Transaction.TestObject;
            var result = await _ndmBridge.CallAsync(transaction, blockNumber);
            _blockchainBridge.Received().FindBlock(blockNumber);
            _blockchainBridge.DidNotReceiveWithAnyArgs().Call(null, null);
            result.Should().BeSameAs(Array.Empty<byte>());
        }
        
        [Test]
        public async Task call_with_transaction_number_should_invoke_blockchain_bridge_call_and_return_data()
        {
            var block = Build.A.Block.TestObject;
            var transaction = Build.A.Transaction.TestObject;
            var data = new byte[] {0, 1, 2};
            _blockchainBridge.FindBlock(block.Number).Returns(block);
            var output = new BlockchainBridge.CallOutput(data, 0, null);
            _blockchainBridge.Call(block.Header, transaction).Returns(output);
            var result = await _ndmBridge.CallAsync(transaction, block.Number);
            _blockchainBridge.Received().FindBlock(block.Number);
            _blockchainBridge.Received().Call(block.Header, transaction);
            result.Should().BeSameAs(data);
        }
        
        [Test]
        public async Task send_own_transaction_should_invoke_blockchain_bridge_send_transaction_and_return_hash()
        {
            var transaction = Build.A.Transaction.TestObject;
            var hash = TestItem.KeccakA;
            _blockchainBridge.SendTransaction(transaction, true).Returns(hash);
            var result = await _ndmBridge.SendOwnTransactionAsync(transaction);
            _blockchainBridge.Received().SendTransaction(transaction, true);
            result.Should().Be(hash);
        }
    }
}