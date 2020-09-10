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
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Int256;
using Nethermind.Facade.Proxy;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Services
{
    public class NdmBlockchainBridgeProxyTests
    {
        private INdmBlockchainBridge _ndmBridge;
        private IEthJsonRpcClientProxy _proxy;

        [SetUp]
        public void Setup()
        {
            _proxy = Substitute.For<IEthJsonRpcClientProxy>();
            _ndmBridge = new NdmBlockchainBridgeProxy(_proxy);
        }
        
        [Test]
        public void constructor_should_throw_exception_if_proxy_argument_is_null()
        {
            Action act = () => _ndmBridge = new NdmBlockchainBridgeProxy(null);
            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public async Task get_latest_block_number_should_invoke_proxy_eth_blockNumber_and_return_0_if_result_is_invalid()
        {
            var result = await _ndmBridge.GetLatestBlockNumberAsync();
            await _proxy.Received().eth_blockNumber();
            result.Should().Be(0);
        }

        [Test]
        public async Task get_latest_block_number_should_invoke_proxy_eth_blockNumber_and_return_number_if_result_is_valid()
        {
            long number = 1;
            _proxy.eth_blockNumber().Returns(RpcResult<long?>.Ok(number));
            var result = await _ndmBridge.GetLatestBlockNumberAsync();
            await _proxy.Received().eth_blockNumber();
            result.Should().Be(number);
        }

        [Test]
        public async Task get_code_should_invoke_proxy_get_code()
        {
            var code = new byte[] {0, 1, 2};
            var address = TestItem.AddressA;
            _proxy.eth_getCode(address, Arg.Is<BlockParameterModel>(x => x.Type == BlockParameterModel.Latest.Type))
                .Returns(RpcResult<byte[]>.Ok(code));
            var result = await _ndmBridge.GetCodeAsync(address);
            await _proxy.Received().eth_getCode(address,
                Arg.Is<BlockParameterModel>(x => x.Type == BlockParameterModel.Latest.Type));
            result.Should().BeSameAs(code);
        }

        [Test]
        public async Task find_block_by_hash_should_invoke_proxy_eth_getBlockByHash()
        {
            var blockModel = GetBlockModel();
            _proxy.eth_getBlockByHash(blockModel.Hash).Returns(RpcResult<BlockModel<Keccak>>.Ok(blockModel));
            var block = await _ndmBridge.FindBlockAsync(blockModel.Hash);
            await _proxy.Received().eth_getBlockByHash(blockModel.Hash);
            block.Should().NotBeNull();
            ValidateBlock(block, blockModel);
        }

        [Test]
        public async Task find_block_by_number_should_invoke_proxy_eth_getBlockByNumber()
        {
            var blockModel = GetBlockModel();
            _proxy.eth_getBlockByNumber(Arg.Is<BlockParameterModel>(x => x.Number == blockModel.Number))
                .Returns(RpcResult<BlockModel<Keccak>>.Ok(blockModel));
            var block = await _ndmBridge.FindBlockAsync((long) blockModel.Number);
            await _proxy.Received()
                .eth_getBlockByNumber(Arg.Is<BlockParameterModel>(x => x.Number == blockModel.Number));
            block.Should().NotBeNull();
            ValidateBlock(block, blockModel);
        }

        [Test]
        public async Task get_latest_block_should_invoke_proxy_eth_getBlockByNumber_with_latest_argument()
        {
            var blockModel = GetBlockModel();
            _proxy.eth_getBlockByNumber(Arg.Is<BlockParameterModel>(x => x.Type == BlockParameterModel.Latest.Type))
                .Returns(RpcResult<BlockModel<Keccak>>.Ok(blockModel));
            var block = await _ndmBridge.GetLatestBlockAsync();
            await _proxy.Received()
                .eth_getBlockByNumber(Arg.Is<BlockParameterModel>(x => x.Type == BlockParameterModel.Latest.Type));
            block.Should().NotBeNull();
            ValidateBlock(block, blockModel);
        }
     
        [Test]
        public async Task get_nonce_should_invoke_proxy_eth_getTransactionCount_with_address_and_pending_arguments()
        {
            UInt256 nonce = 1;
            var address = TestItem.AddressA;
            _proxy.eth_getTransactionCount(address,
                    Arg.Is<BlockParameterModel>(x => x.Type == BlockParameterModel.Pending.Type))
                .Returns(RpcResult<UInt256?>.Ok(nonce));
            var result = await _ndmBridge.GetNonceAsync(address);
            await _proxy.Received()
                .eth_getTransactionCount(address,
                    Arg.Is<BlockParameterModel>(x => x.Type == BlockParameterModel.Pending.Type));
            result.Should().Be(nonce);
        }

        [Test]
        public async Task get_transaction_should_invoke_proxy_eth_getTransactionByHash_and_eth_getTransactionReceipt_and_return_null_if_receipt_or_transaction_is_null()
        {
            var hash = TestItem.KeccakA;
            var result = await _ndmBridge.GetTransactionAsync(hash);
            await _proxy.Received().eth_getTransactionByHash(hash);
            await _proxy.Received().eth_getTransactionReceipt(hash);
            result.Should().BeNull();
        }

        [Test]
        public async Task get_transaction_should_invoke_proxy_eth_getTransactionByHash_and_eth_getTransactionReceipt_and_return_ndm_transaction()
        {
            var hash = TestItem.KeccakA;
            var transaction = Build.A.Transaction.TestObject;
            var receipt = Build.A.Receipt.TestObject;
            var transactionModel = new TransactionModel
            {
                Hash = transaction.Hash,
                Nonce = transaction.Nonce,
                BlockHash = receipt.BlockHash,
                BlockNumber = (UInt256) receipt.BlockNumber,
                From = transaction.SenderAddress,
                To = transaction.To,
                Gas = (UInt256) transaction.GasLimit,
                GasPrice = transaction.GasPrice,
                Input = transaction.Data,
                Value = transaction.Value
            };
            var receiptModel = new ReceiptModel
            {
                GasUsed = (UInt256) receipt.GasUsed
            };
            _proxy.eth_getTransactionByHash(hash).Returns(RpcResult<TransactionModel>.Ok(transactionModel));
            _proxy.eth_getTransactionReceipt(hash).Returns(RpcResult<ReceiptModel>.Ok(receiptModel));
            var result = await _ndmBridge.GetTransactionAsync(hash);
            await _proxy.Received().eth_getTransactionByHash(hash);
            await _proxy.Received().eth_getTransactionReceipt(hash);
            result.Should().NotBeNull();
            result.Transaction.Should().NotBeNull();
            result.BlockNumber.Should().Be(receipt.BlockNumber);
            result.BlockHash.Should().Be(receipt.BlockHash);
            result.GasUsed.Should().Be(receipt.GasUsed);
            result.Transaction.Hash.Should().Be(transaction.Hash);
            result.Transaction.Nonce.Should().Be(transaction.Nonce);
            result.Transaction.SenderAddress.Should().Be(transaction.SenderAddress);
            result.Transaction.To.Should().Be(transaction.To);
            result.Transaction.GasLimit.Should().Be(transaction.GasLimit);
            result.Transaction.GasPrice.Should().Be(transaction.GasPrice);
            result.Transaction.Data.Should().BeSameAs(transaction.Data);
            result.Transaction.Value.Should().Be(transaction.Value);
        }
        
        [Test]
        public async Task get_network_id_should_invoke_proxy_eth_chainId()
        {
            const int networkId = 1;
            _proxy.eth_chainId().Returns(RpcResult<UInt256>.Ok(networkId));
            var result = await _ndmBridge.GetNetworkIdAsync();
            await _proxy.Received().eth_chainId();
            result.Should().Be(networkId);
        }

        [Test]
        public async Task call_should_invoke_proxy_eth_call_with_transaction_argument()
        {
            var transaction = Build.A.Transaction.TestObject;
            var data = new byte[] {0, 1, 2};
            var callModel = CallTransactionModel.FromTransaction(transaction);
            _proxy.eth_call(Arg.Is<CallTransactionModel>(x => x.From == callModel.From &&
                                                              x.To == callModel.To &&
                                                              x.Gas == callModel.Gas &&
                                                              x.GasPrice == callModel.GasPrice &&
                                                              x.Value == callModel.Value &&
                                                              x.Data.SequenceEqual(callModel.Data)))
                .Returns(RpcResult<byte[]>.Ok(data));
            var result = await _ndmBridge.CallAsync(transaction);
            await _proxy.Received().eth_call(Arg.Any<CallTransactionModel>());
            result.Should().BeSameAs(data);
        }

        [Test]
        public async Task call_with_transaction_number_for_invalid_block_should_invoke_proxy_eth_call_and_return_empty_data()
        {
            const int blockNumber = 1;
            var transaction = Build.A.Transaction.TestObject;
            var result = await _ndmBridge.CallAsync(transaction, blockNumber);
            await _proxy.Received().eth_call(Arg.Any<CallTransactionModel>(),
                Arg.Is<BlockParameterModel>(x => x.Number == blockNumber));
            result.Should().BeSameAs(Array.Empty<byte>());
        }
   
        [Test]
        public async Task call_with_transaction_number_should_invoke_proxy_eth_call_and_return_data()
        {
            const int blockNumber = 1;
            var transaction = Build.A.Transaction.TestObject;
            var data = new byte[] {0, 1, 2};
            var callModel = CallTransactionModel.FromTransaction(transaction);
            _proxy.eth_call(Arg.Is<CallTransactionModel>(x => x.From == callModel.From &&
                                                              x.To == callModel.To &&
                                                              x.Gas == callModel.Gas &&
                                                              x.GasPrice == callModel.GasPrice &&
                                                              x.Value == callModel.Value &&
                                                              x.Data.SequenceEqual(callModel.Data)),
                    Arg.Is<BlockParameterModel>(x => x.Number == blockNumber))
                .Returns(RpcResult<byte[]>.Ok(data));
            var result = await _ndmBridge.CallAsync(transaction, blockNumber);
            await _proxy.Received().eth_call(Arg.Any<CallTransactionModel>(),
                Arg.Is<BlockParameterModel>(x => x.Number == blockNumber));
            result.Should().BeSameAs(data);
        }

        [Test]
        public async Task send_own_transaction_should_invoke_proxy_eth_sendRawTransaction_and_return_hash()
        {
            var transaction = Build.A.Transaction.TestObject;
            var data = Rlp.Encode(transaction).Bytes;
            var hash = TestItem.KeccakA;
            _proxy.eth_sendRawTransaction(Arg.Is<byte[]>(x => x.SequenceEqual(data)))
                .Returns(RpcResult<Keccak>.Ok(hash));
            var result = await _ndmBridge.SendOwnTransactionAsync(transaction);
            await _proxy.Received().eth_sendRawTransaction(Arg.Is<byte[]>(x => x.SequenceEqual(data)));
            result.Should().BeSameAs(hash);
        }

        private static void ValidateBlock(Block block, BlockModel<Keccak> model)
        {
            block.Header.ParentHash.Should().Be(model.ParentHash);
            block.Header.OmmersHash.Should().Be(model.Sha3Uncles);
            block.Header.Beneficiary.Should().Be(model.Miner);
            block.Header.Difficulty.Should().Be(model.Difficulty);
            block.Header.Number.Should().Be((long)model.Number);
            block.Header.GasLimit.Should().Be((long)model.GasLimit);
            block.Header.Timestamp.Should().Be(model.Timestamp);
            block.Header.ExtraData.Should().BeSameAs(model.ExtraData);
            block.StateRoot.Should().Be(model.StateRoot);
            block.GasUsed.Should().Be((long)model.GasUsed);
            block.Hash.Should().Be(model.Hash);
            block.MixHash.Should().Be(model.MixHash);
            block.Nonce.Should().Be((ulong)model.Nonce);
            block.ReceiptsRoot.Should().Be(model.ReceiptsRoot);
            block.TotalDifficulty.Should().Be(model.TotalDifficulty);
            block.TxRoot.Should().Be(model.TransactionsRoot);
        }

        private BlockModel<Keccak> GetBlockModel()
            => new BlockModel<Keccak>
            {
                Difficulty = 1,
                ExtraData = new byte[] {0, 1, 2},
                Hash = TestItem.KeccakA,
                Miner = TestItem.AddressA,
                Nonce = 2,
                Number = 3,
                Size = 4,
                Timestamp = 5,
                Transactions = new List<Keccak>
                {
                    TestItem.KeccakB
                },
                GasLimit = 6,
                GasUsed = 7,
                MixHash = TestItem.KeccakD,
                ParentHash = TestItem.KeccakE,
                ReceiptsRoot = TestItem.KeccakF,
                Sha3Uncles = TestItem.KeccakG,
                StateRoot = TestItem.KeccakH,
                TotalDifficulty = 8,
                TransactionsRoot = TestItem.KeccakA
            };
    }
}
