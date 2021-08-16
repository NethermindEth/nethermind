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
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Int256;
using Nethermind.Facade;
using Nethermind.State;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Services
{
    public class NdmBlockchainBridgeTests
    {
        private INdmBlockchainBridge _ndmBridge;
        private IBlockchainBridge _blockchainBridge;
        private ITxPool _txPool;
        private ITxSender _txSender;
        private IBlockFinder _blockFinder;
        private IStateReader _stateReader;

        [SetUp]
        public void Setup()
        {
            _blockchainBridge = Substitute.For<IBlockchainBridge>();
            _txPool = Substitute.For<ITxPool>();
            _blockFinder = Substitute.For<IBlockFinder>();
            _stateReader = Substitute.For<IStateReader>();
            _txSender = Substitute.For<ITxSender>();
            
            _ndmBridge = new NdmBlockchainBridge(_blockchainBridge, _blockFinder, _stateReader, _txSender);
        }

        [Test]
        public async Task get_latest_block_number_should_return_0_if_head_is_null()
        {
            long result = await _ndmBridge.GetLatestBlockNumberAsync();
            result.Should().Be(0);
        }
        
        [Test]
        public async Task get_latest_block_number_should_return_head_number()
        {
            Block header = Build.A.Block.TestObject;
            _blockFinder.Head.Returns(header);
            long result = await _ndmBridge.GetLatestBlockNumberAsync();
            result.Should().Be(_blockFinder.Head.Number);
        }

        private Block _anyBlock = Build.A.Block.TestObject;
        
        [Test]
        public async Task get_code_should_invoke_blockchain_bridge_get_code()
        {
            byte[] code = new byte[] {0, 1, 2};
            Address address = TestItem.AddressA;
            _stateReader.GetCode(Arg.Any<Keccak>(), address).Returns(code);
            _blockFinder.Head.Returns(_anyBlock);
            byte[] result = await _ndmBridge.GetCodeAsync(address);
            _stateReader.Received().GetCode(_anyBlock.StateRoot, address);
            result.Should().BeSameAs(code);
        }
        
        [Test]
        public async Task find_block_by_hash_should_invoke_blockchain_bridge_find_block_by_hash()
        {
            Block block = Build.A.Block.TestObject;
            _blockFinder.FindBlock(block.Hash).Returns(block);
            Block? result = await _ndmBridge.FindBlockAsync(block.Hash);
            _blockFinder.Received().FindBlock(block.Hash);
            result.Should().Be(block);
        }
        
        [Test]
        public async Task find_block_by_number_should_invoke_blockchain_bridge_find_block_by_number()
        {
            Block block = Build.A.Block.TestObject;
            _blockFinder.FindBlock(block.Number).Returns(block);
            Block? result = await _ndmBridge.FindBlockAsync(block.Number);
            _blockFinder.Received().FindBlock(block.Number);
            result.Should().Be(block);
        }
        
        [Test]
        public async Task get_latest_block_number_should_return_null_if_head_is_null()
        {
            Block? result = await _ndmBridge.GetLatestBlockAsync();
            result.Should().BeNull();
        }
        
        [Test]
        public async Task get_latest_block_should_return_head_number()
        {
            Block block = Build.A.Block.TestObject;
            _blockchainBridge.BeamHead.Returns(block);
            _blockFinder.FindBlock(block.Hash).Returns(block);
            Block? result = await _ndmBridge.GetLatestBlockAsync();
            result.Should().Be(block);
            _blockFinder.Received().FindBlock(block.Hash);
        }
        
        [Test]
        public async Task get_nonce_should_invoke_blockchain_bridge_get_nonce()
        {
            UInt256 nonce = 1;
            Address address = TestItem.AddressA;
            _blockchainBridge.BeamHead.Returns(_anyBlock);
            _stateReader.GetAccount(_anyBlock.StateRoot, address).Returns(Account.TotallyEmpty.WithChangedNonce(nonce));
            UInt256 result = await _ndmBridge.GetNonceAsync(address);
            _stateReader.Received().GetNonce(_anyBlock.StateRoot, address);
            result.Should().Be(nonce);
        }

        [Test]
        public async Task get_transaction_should_return_null_if_receipt_or_transaction_is_null()
        {
            Keccak hash = TestItem.KeccakA;
            NdmTransaction? result = await _ndmBridge.GetTransactionAsync(hash);
            result.Should().BeNull();
            _blockchainBridge.Received().GetTransaction(hash);
        }

        [Test]
        public async Task get_transaction_should_invoke_blockchain_bridge_get_transaction_and_return_ndm_transaction()
        {
            TxReceipt receipt = Build.A.Receipt.TestObject;
            Transaction transaction = Build.A.Transaction.TestObject;
            UInt256? baseFee = null;
            (TxReceipt receipt, Transaction transaction, UInt256? baseFee) tuple = (receipt, transaction, baseFee);
            Keccak hash = TestItem.KeccakA;
            _blockchainBridge.GetTransaction(hash).Returns(tuple);
            NdmTransaction? result = await _ndmBridge.GetTransactionAsync(hash);
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
            const ulong networkId = 1;
            _blockchainBridge.GetChainId().Returns(networkId);
            ulong result = await _ndmBridge.GetNetworkIdAsync();
            _blockchainBridge.Received().GetChainId();
            result.Should().Be(networkId);
        }
        
        [Test]
        public async Task call_should_invoke_blockchain_bridge_call_and_return_data()
        {
            Block head = Build.A.Block.TestObject;
            Transaction transaction = Build.A.Transaction.TestObject;
            byte[] data = new byte[] {0, 1, 2};
            _blockchainBridge.BeamHead.Returns(head);
            BlockchainBridge.CallOutput output = new BlockchainBridge.CallOutput(data, 0, null);
            _blockchainBridge.Call(head?.Header, transaction, default).Returns(output);
            byte[] result = await _ndmBridge.CallAsync(transaction);
            _blockchainBridge.Received().Call(head?.Header, transaction, default);
            result.Should().BeSameAs(data);
        }
        
        [Test]
        public async Task call_with_transaction_number_for_invalid_block_should_invoke_blockchain_bridge_call_and_return_empty_data()
        {
            const int blockNumber = 1;
            Transaction transaction = Build.A.Transaction.TestObject;
            byte[] result = await _ndmBridge.CallAsync(transaction, blockNumber);
            _blockFinder.Received().FindBlock(blockNumber);
            _blockchainBridge.DidNotReceiveWithAnyArgs().Call(null, null, default);
            result.Should().BeSameAs(Array.Empty<byte>());
        }
        
        [Test]
        public async Task call_with_transaction_number_should_invoke_blockchain_bridge_call_and_return_data()
        {
            Block block = Build.A.Block.TestObject;
            Transaction transaction = Build.A.Transaction.TestObject;
            byte[] data = new byte[] {0, 1, 2};
            _blockFinder.FindBlock(block.Number).Returns(block);
            BlockchainBridge.CallOutput output = new BlockchainBridge.CallOutput(data, 0, null);
            _blockchainBridge.Call(block.Header, transaction, default).Returns(output);
            byte[] result = await _ndmBridge.CallAsync(transaction, block.Number);
            _blockFinder.Received().FindBlock(block.Number);
            _blockchainBridge.Received().Call(block.Header, transaction, default);
            result.Should().BeSameAs(data);
        }
        
        [Test]
        public async Task send_own_transaction_should_invoke_blockchain_bridge_send_transaction_and_return_hash()
        {
            Transaction transaction = Build.A.Transaction.TestObject;
            Keccak hash = TestItem.KeccakA;
            _txSender.SendTransaction(transaction, TxHandlingOptions.PersistentBroadcast | TxHandlingOptions.ManagedNonce).Returns((hash, null));
            Keccak? result = await _ndmBridge.SendOwnTransactionAsync(transaction);
            await _txSender.Received().SendTransaction(transaction, TxHandlingOptions.PersistentBroadcast | TxHandlingOptions.ManagedNonce);
            result.Should().Be(hash);
        }
    }
}
