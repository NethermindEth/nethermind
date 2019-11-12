/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using Nethermind.Facade;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    public class TraceModuleTests
    {
        [Test]
        public void Trace_replay_transaction()
        {
            ParityLikeTxTrace result = BuildParityTxTrace();

            ITracer tracer = Substitute.For<ITracer>();
            tracer.ParityTrace(TestItem.KeccakC, Arg.Any<ParityTraceTypes>()).Returns(result);

            ITraceModule module = new TraceModule(Substitute.For<IBlockchainBridge>(), NullLogManager.Instance, tracer);

            string serialized = RpcTest.TestSerializedRequest(TraceModuleFactory.Converters, module, "trace_replayTransaction", TestItem.KeccakC.ToString(true), "[\"stateDiff\", \"trace\"]");

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":{\"output\":null,\"stateDiff\":{\"0x76e68a8696537e4141926f3e528733af9e237d69\":{\"balance\":{\"*\":{\"from\":\"0x1\",\"to\":\"0x2\"}},\"code\":{\"*\":{\"from\":\"0x01\",\"to\":\"0x02\"}},\"nonce\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x1\"}},\"storage\":{\"0x0000000000000000000000000000000000000000000000000000000000000001\":{\"*\":{\"from\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"to\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"}}}}},\"trace\":[{\"action\":{\"callType\":\"init\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"input\":\"0x010203040506\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x3039\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"traceAddress\":[0,0],\"type\":null}],\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"vmTrace\":null}}", serialized);
        }

        [Test]
        public void Trace_replay_block()
        {
            ParityLikeTxTrace result1 = BuildParityTxTrace();
            ParityLikeTxTrace result2 = BuildParityTxTrace();

            Block block = Build.A.Block.TestObject;
            IBlockchainBridge blockchainBridge = Substitute.For<IBlockchainBridge>();
            blockchainBridge.FindLatestBlock().Returns(block);

            ITracer tracer = Substitute.For<ITracer>();
            tracer.ParityTraceBlock(block.Hash, Arg.Any<ParityTraceTypes>()).Returns(new[] {result1, result2});

            ITraceModule module = new TraceModule(blockchainBridge, NullLogManager.Instance, tracer);

            string serialized = RpcTest.TestSerializedRequest(TraceModuleFactory.Converters, module, "trace_replayBlockTransactions", "latest", "[\"stateDiff\", \"trace\"]");

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":[{\"output\":null,\"stateDiff\":{\"0x76e68a8696537e4141926f3e528733af9e237d69\":{\"balance\":{\"*\":{\"from\":\"0x1\",\"to\":\"0x2\"}},\"code\":{\"*\":{\"from\":\"0x01\",\"to\":\"0x02\"}},\"nonce\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x1\"}},\"storage\":{\"0x0000000000000000000000000000000000000000000000000000000000000001\":{\"*\":{\"from\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"to\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"}}}}},\"trace\":[{\"action\":{\"callType\":\"init\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"input\":\"0x010203040506\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x3039\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"traceAddress\":[0,0],\"type\":null}],\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"vmTrace\":null},{\"output\":null,\"stateDiff\":{\"0x76e68a8696537e4141926f3e528733af9e237d69\":{\"balance\":{\"*\":{\"from\":\"0x1\",\"to\":\"0x2\"}},\"code\":{\"*\":{\"from\":\"0x01\",\"to\":\"0x02\"}},\"nonce\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x1\"}},\"storage\":{\"0x0000000000000000000000000000000000000000000000000000000000000001\":{\"*\":{\"from\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"to\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"}}}}},\"trace\":[{\"action\":{\"callType\":\"init\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"input\":\"0x010203040506\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x3039\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"traceAddress\":[0,0],\"type\":null}],\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"vmTrace\":null}]}", serialized);
        }

        [Test]
        public void Trace_replayBlockTransactions_null()
        {
            IBlockchainBridge blockchainBridge = Substitute.For<IBlockchainBridge>();
            ITracer tracer = Substitute.For<ITracer>();

            ITraceModule module = new TraceModule(blockchainBridge, NullLogManager.Instance, tracer);

            string serialized = RpcTest.TestSerializedRequest(TraceModuleFactory.Converters, module, "trace_replayBlockTransactions", "earliest", "[]");

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":null}", serialized);
        }

        [Test]
        public void Trace_block()
        {
            ParityLikeTxTrace result1 = BuildParityTxTrace();
            ParityLikeTxTrace result2 = BuildParityTxTrace();

            Block block = Build.A.Block.TestObject;
            IBlockchainBridge blockchainBridge = Substitute.For<IBlockchainBridge>();
            blockchainBridge.FindEarliestBlock().Returns(block);

            ITracer tracer = Substitute.For<ITracer>();
            tracer.ParityTraceBlock(block.Hash, Arg.Any<ParityTraceTypes>()).Returns(new[] {result1, result2});

            ITraceModule module = new TraceModule(blockchainBridge, NullLogManager.Instance, tracer);

            string serialized = RpcTest.TestSerializedRequest(TraceModuleFactory.Converters, module, "trace_block", "earliest");

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":[{\"output\":null,\"stateDiff\":{\"0x76e68a8696537e4141926f3e528733af9e237d69\":{\"balance\":{\"*\":{\"from\":\"0x1\",\"to\":\"0x2\"}},\"code\":{\"*\":{\"from\":\"0x01\",\"to\":\"0x02\"}},\"nonce\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x1\"}},\"storage\":{\"0x0000000000000000000000000000000000000000000000000000000000000001\":{\"*\":{\"from\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"to\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"}}}}},\"trace\":[{\"action\":{\"callType\":\"init\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"input\":\"0x010203040506\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x3039\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"traceAddress\":[0,0],\"type\":null}],\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"vmTrace\":null},{\"output\":null,\"stateDiff\":{\"0x76e68a8696537e4141926f3e528733af9e237d69\":{\"balance\":{\"*\":{\"from\":\"0x1\",\"to\":\"0x2\"}},\"code\":{\"*\":{\"from\":\"0x01\",\"to\":\"0x02\"}},\"nonce\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x1\"}},\"storage\":{\"0x0000000000000000000000000000000000000000000000000000000000000001\":{\"*\":{\"from\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"to\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"}}}}},\"trace\":[{\"action\":{\"callType\":\"init\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"input\":\"0x010203040506\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x3039\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"traceAddress\":[0,0],\"type\":null}],\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"vmTrace\":null}]}", serialized);
        }

        [Test]
        public void Trace_raw_transaction()
        {
            ParityLikeTxTrace result1 = BuildParityTxTrace();
            ParityLikeTxTrace result2 = BuildParityTxTrace();

            Block block = Build.A.Block.TestObject;
            IBlockchainBridge blockchainBridge = Substitute.For<IBlockchainBridge>();
            blockchainBridge.FindEarliestBlock().Returns(block);

            ITracer tracer = Substitute.For<ITracer>();
            tracer.ParityTraceBlock(block.Hash, Arg.Any<ParityTraceTypes>()).Returns(new[] {result1, result2});

            ITraceModule module = new TraceModule(blockchainBridge, NullLogManager.Instance, tracer);

            string serialized = RpcTest.TestSerializedRequest(TraceModuleFactory.Converters, module, "trace_rawTransaction", "0xd46e8dd67c5d32be8d46e8dd67c5d32be8058bb8eb970870f072445675058bb8eb970870f072445675", "[\"trace\"]");

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":null}", serialized);
        }


        [Test]
        public void Trace_block_null()
        {
            IBlockchainBridge blockchainBridge = Substitute.For<IBlockchainBridge>();
            ITracer tracer = Substitute.For<ITracer>();

            ITraceModule module = new TraceModule(blockchainBridge, NullLogManager.Instance, tracer);

            string serialized = RpcTest.TestSerializedRequest(TraceModuleFactory.Converters, module, "trace_block", "earliest");

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":null}", serialized);
        }

        private static ParityLikeTxTrace BuildParityTxTrace()
        {
            ParityTraceAction subtrace = new ParityTraceAction();
            subtrace.Value = 67890;
            subtrace.CallType = "call";
            subtrace.From = TestItem.AddressC;
            subtrace.To = TestItem.AddressD;
            subtrace.Input = Bytes.Empty;
            subtrace.Gas = 10000;
            subtrace.TraceAddress = new int[] {0, 0};

            ParityLikeTxTrace result = new ParityLikeTxTrace();
            result.Action = new ParityTraceAction();
            result.Action.Value = 12345;
            result.Action.CallType = "init";
            result.Action.From = TestItem.AddressA;
            result.Action.To = TestItem.AddressB;
            result.Action.Input = new byte[] {1, 2, 3, 4, 5, 6};
            result.Action.Gas = 40000;
            result.Action.TraceAddress = new int[] {0};
            result.Action.Subtraces.Add(subtrace);

            result.BlockHash = TestItem.KeccakB;
            result.BlockNumber = 123456;
            result.TransactionHash = TestItem.KeccakC;
            result.TransactionPosition = 5;
            result.Action.TraceAddress = new int[] {1, 2, 3};

            ParityAccountStateChange stateChange = new ParityAccountStateChange();
            stateChange.Balance = new ParityStateChange<UInt256?>(1, 2);
            stateChange.Nonce = new ParityStateChange<UInt256?>(0, 1);
            stateChange.Storage = new Dictionary<UInt256, ParityStateChange<byte[]>>();
            stateChange.Storage[1] = new ParityStateChange<byte[]>(new byte[] {1}, new byte[] {2});
            stateChange.Code = new ParityStateChange<byte[]>(new byte[] {1}, new byte[] {2});

            result.StateChanges = new Dictionary<Address, ParityAccountStateChange>();
            result.StateChanges.Add(TestItem.AddressC, stateChange);
            return result;
        }
    }
}