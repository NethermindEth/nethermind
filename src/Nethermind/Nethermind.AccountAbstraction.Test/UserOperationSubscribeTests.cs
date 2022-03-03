//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System;
using System.Threading;
using FluentAssertions;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Source;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.AccountAbstraction.Subscribe;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.TxPool;

namespace Nethermind.AccountAbstraction.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class UserOperationSubscribeTests
    {
        private ISubscribeRpcModule _subscribeRpcModule = null!;
        private ILogManager _logManager = null!;
        private IBlockTree _blockTree = null!;
        private ITxPool _txPool = null!;
        private IReceiptStorage _receiptStorage = null!;
        private IFilterStore _filterStore = null!;
        private ISubscriptionManager _subscriptionManager = null!;
        private IJsonRpcDuplexClient _jsonRpcDuplexClient = null!;
        private IJsonSerializer _jsonSerializer = null!;
        private ISpecProvider _specProvider = null!;
        private IUserOperationPool _userOperationPool = null!;

        [SetUp]
        public void Setup()
        {
            _logManager = Substitute.For<ILogManager>();
            _blockTree = Substitute.For<IBlockTree>();
            _txPool = Substitute.For<ITxPool>();
            _receiptStorage = Substitute.For<IReceiptStorage>();
            _specProvider = Substitute.For<ISpecProvider>();
            _userOperationPool = Substitute.For<IUserOperationPool>();
            _filterStore = new FilterStore();
            _jsonRpcDuplexClient = Substitute.For<IJsonRpcDuplexClient>();
            _jsonSerializer = new EthereumJsonSerializer();
            
            SubscriptionFactory subscriptionFactory = new(
                _logManager,
                _blockTree,
                _txPool,
                _receiptStorage,
                _filterStore,
                new EthSyncingInfo(_blockTree),
                _specProvider);
            
            subscriptionFactory.RegisterSubscriptionType(
                "newPendingUserOperations",
                () => new NewPendingUserOpsSubscription(
                    _jsonRpcDuplexClient,
                    _userOperationPool,
                    _logManager)
            );
            
            _subscriptionManager = new SubscriptionManager(
                subscriptionFactory,
                _logManager);
            
            _subscribeRpcModule = new SubscribeRpcModule(_subscriptionManager);
            _subscribeRpcModule.Context = new JsonRpcContext(RpcEndpoint.Ws, _jsonRpcDuplexClient);
        }

        private JsonRpcResult GetNewPendingUserOpsResult(UserOperationEventArgs userOperationEventArgs,
            out string subscriptionId)
        {
            NewPendingUserOpsSubscription newPendingUserOpsSubscription =
                new(_jsonRpcDuplexClient, _userOperationPool, _logManager);
            JsonRpcResult jsonRpcResult = new();

            ManualResetEvent manualResetEvent = new(false);
            newPendingUserOpsSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResult = j;
                manualResetEvent.Set();
            }));

            _userOperationPool!.NewPending += Raise.EventWith(new object(), userOperationEventArgs);
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(100));

            subscriptionId = newPendingUserOpsSubscription.Id;
            return jsonRpcResult;
        }

        [Test]
        public void NewPendingUserOperationsSubscription_creating_result()
        {
            string serialized = RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "newPendingUserOperations");
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44,34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void NewPendingUserOperationsSubscription_on_NewPending_event()
        {
            UserOperation userOperation = Build.A.UserOperation.TestObject;
            UserOperationEventArgs userOperationEventArgs = new(userOperation);

            JsonRpcResult jsonRpcResult = GetNewPendingUserOpsResult(userOperationEventArgs, out var subscriptionId);

            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            var expectedResult = "{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"" + subscriptionId + "\",\"result\":{\"sender\":\"0x0000000000000000000000000000000000000000\",\"nonce\":\"0x0\",\"callData\":\"0x\",\"initCode\":\"0x\",\"callGas\":\"0xf4240\",\"verificationGas\":\"0xf4240\",\"preVerificationGas\":\"0x33450\",\"maxFeePerGas\":\"0x1\",\"maxPriorityFeePerGas\":\"0x1\",\"paymaster\":\"0x0000000000000000000000000000000000000000\",\"signature\":\"0x\",\"paymasterData\":\"0x\"}}}";

            expectedResult.Should().Be(serialized);
        }
        
        [Test]
        public void Wrong_subscription_name()
        {
            string serialized = RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "whatever");
            var expectedResult = "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Wrong subscription type: whatever.\"},\"id\":67}";
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void No_subscription_name()
        {
            string serialized = RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe");
            var expectedResult = "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"Invalid params\",\"data\":\"Incorrect parameters count, expected: 2, actual: 0\"},\"id\":67}";
            expectedResult.Should().Be(serialized);
        }
        
        [Test]
        public void Eth_unsubscribe_success()
        {
            string serializedSub = RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "newPendingUserOperations");
            string subscriptionId = serializedSub.Substring(serializedSub.Length - 44, 34);
            string expectedSub = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", subscriptionId, "\",\"id\":67}");
            expectedSub.Should().Be(serializedSub);

            string serializedUnsub = RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_unsubscribe", subscriptionId);
            string expectedUnsub = "{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}";

            expectedUnsub.Should().Be(serializedUnsub);
        }

        [Test]
        public void Subscriptions_remove_after_closing_websockets_client()
        {
            string serialized = RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "newPendingUserOperations");
            string subscriptionId = serialized.Substring(serialized.Length - 44, 34);
            string expectedId = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", subscriptionId, "\",\"id\":67}");
            expectedId.Should().Be(serialized);

            _jsonRpcDuplexClient.Closed += Raise.Event();

            string serializedLogsUnsub = RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_unsubscribe", subscriptionId);
            string expectedLogsUnsub =
                string.Concat("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Failed to unsubscribe: ",
                    subscriptionId, ".\",\"data\":false},\"id\":67}");
            expectedLogsUnsub.Should().Be(serializedLogsUnsub);
        }
    }
}
