// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Facade.Proxy;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Facade.Proxy.Models.MultiCall;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Facade.Test.Proxy
{
    public class EthJsonRpcClientProxyTests
    {
        private IEthJsonRpcClientProxy _proxy;
        private IJsonRpcClientProxy _client;

        [SetUp]
        public void Setup()
        {
            _client = Substitute.For<IJsonRpcClientProxy>();
            _proxy = new EthJsonRpcClientProxy(_client);
        }

        [Test]
        public void constructor_should_throw_exception_if_client_argument_is_null()
        {
            Action act = () => _proxy = new EthJsonRpcClientProxy(null);
            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public async Task eth_chainId_should_invoke_client_method()
        {
            await _proxy.eth_chainId();
            await _client.Received().SendAsync<UInt256>(nameof(_proxy.eth_chainId));
        }

        [Test]
        public async Task eth_blockNumber_should_invoke_client_method()
        {
            await _proxy.eth_blockNumber();
            await _client.Received().SendAsync<long?>(nameof(_proxy.eth_blockNumber));
        }

        [Test]
        public async Task eth_getBalance_should_invoke_client_method()
        {
            var address = TestItem.AddressA;
            await _proxy.eth_getBalance(address);
            await _client.Received().SendAsync<UInt256?>(nameof(_proxy.eth_getBalance),
                address, null);
        }

        [Test]
        public async Task eth_getTransactionCount_should_invoke_client_method()
        {
            var address = TestItem.AddressA;
            var blockParameter = BlockParameterModel.Latest;
            await _proxy.eth_getTransactionCount(address, blockParameter);
            await _client.Received().SendAsync<UInt256>(nameof(_proxy.eth_getTransactionCount),
                address, blockParameter.Type);
        }

        [Test]
        public async Task eth_getTransactionReceipt_should_invoke_client_method()
        {
            var hash = TestItem.KeccakA;
            await _proxy.eth_getTransactionReceipt(hash);
            await _client.Received().SendAsync<ReceiptModel>(nameof(_proxy.eth_getTransactionReceipt), hash);
        }

        [Test]
        public async Task eth_call_should_invoke_client_method()
        {
            var callTransaction = new CallTransactionModel();
            var blockParameter = BlockParameterModel.Latest;
            await _proxy.eth_call(callTransaction, blockParameter);
            await _client.Received().SendAsync<byte[]>(nameof(_proxy.eth_call),
                callTransaction, blockParameter.Type);
        }


        [Test]
        public async Task eth_multicall_should_invoke_client_method()
        {
            var multyCallTransaction = new MultiCallBlockStateCallsModel[] { };
            var blockParameter = BlockParameterModel.Latest;
            var traceTransactions = true;
            await _proxy.eth_multicall(1, multyCallTransaction, blockParameter, traceTransactions);

            var calls = _client.ReceivedCalls().ToList();
            await _client.Received().SendAsync<MultiCallBlockResult[]>(nameof(_proxy.eth_multicall),
                (ulong)1, multyCallTransaction, blockParameter.Type, traceTransactions);
        }

        [Test]
        public async Task eth_getCode_should_invoke_client_method()
        {
            var address = TestItem.AddressA;
            var blockParameter = BlockParameterModel.Latest;
            await _proxy.eth_getCode(address, blockParameter);
            await _client.Received().SendAsync<byte[]>(nameof(_proxy.eth_getCode),
                address, blockParameter.Type);
        }

        [Test]
        public async Task eth_getTransactionByHash_should_invoke_client_method()
        {
            var hash = TestItem.KeccakA;
            await _proxy.eth_getTransactionByHash(hash);
            await _client.Received().SendAsync<TransactionModel>(nameof(_proxy.eth_getTransactionByHash), hash);
        }

        [Test]
        public async Task eth_pendingTransactions_should_invoke_client_method()
        {
            await _proxy.eth_pendingTransactions();
            await _client.Received().SendAsync<TransactionModel[]>(nameof(_proxy.eth_pendingTransactions));
        }

        [Test]
        public async Task eth_getBlockByHash_should_invoke_client_method()
        {
            var hash = TestItem.KeccakA;
            const bool returnFullTransactionObjects = true;
            await _proxy.eth_getBlockByHash(hash, returnFullTransactionObjects);
            await _client.Received().SendAsync<BlockModel<Keccak>>(nameof(_proxy.eth_getBlockByHash),
                hash, returnFullTransactionObjects);
        }

        [Test]
        public async Task eth_getBlockByNumber_should_invoke_client_method()
        {
            var blockParameter = BlockParameterModel.FromNumber(1L);
            const bool returnFullTransactionObjects = true;
            await _proxy.eth_getBlockByNumber(blockParameter, returnFullTransactionObjects);
            await _client.Received().SendAsync<BlockModel<Keccak>>(nameof(_proxy.eth_getBlockByNumber),
                blockParameter.Number, returnFullTransactionObjects);
        }
    }
}
