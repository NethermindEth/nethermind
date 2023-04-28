// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class DebugModuleTests
    {
        private IJsonRpcConfig jsonRpcConfig = new JsonRpcConfig();
        private IDebugBridge debugBridge = Substitute.For<IDebugBridge>();

        [Test]
        public void Get_from_db()
        {
            byte[] key = new byte[] { 1, 2, 3 };
            byte[] value = new byte[] { 4, 5, 6 };
            debugBridge.GetDbValue(Arg.Any<string>(), Arg.Any<byte[]>()).Returns(value);

            IConfigProvider configProvider = Substitute.For<IConfigProvider>();
            DebugRpcModule rpcModule = new(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            JsonRpcSuccessResponse? response =
                RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getFromDb", "STATE", key.ToHexString(true)) as
                    JsonRpcSuccessResponse;

            byte[]? result = response?.Result as byte[];
        }

        [Test]
        public void Get_from_db_null_value()
        {
            byte[] key = new byte[] { 1, 2, 3 };
            debugBridge.GetDbValue(Arg.Any<string>(), Arg.Any<byte[]>()).Returns((byte[])null!);

            IConfigProvider configProvider = Substitute.For<IConfigProvider>();
            DebugRpcModule rpcModule = new(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            JsonRpcSuccessResponse? response =
                RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getFromDb", "STATE", key.ToHexString(true)) as
                    JsonRpcSuccessResponse;

            Assert.NotNull(response);
        }

        [TestCase("1")]
        [TestCase("0x1")]
        public void Get_chain_level(string parameter)
        {
            debugBridge.GetLevelInfo(1).Returns(
                new ChainLevelInfo(
                    true,
                    new[]
                    {
                        new BlockInfo(TestItem.KeccakA, 1000),
                        new BlockInfo(TestItem.KeccakB, 1001),
                    }));

            DebugRpcModule rpcModule = new(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            JsonRpcSuccessResponse? response = RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getChainLevel", parameter) as JsonRpcSuccessResponse;
            ChainLevelForRpc? chainLevel = response?.Result as ChainLevelForRpc;
            Assert.NotNull(chainLevel);
            Assert.AreEqual(true, chainLevel?.HasBlockOnMainChain);
            Assert.AreEqual(2, chainLevel?.BlockInfos.Length);
        }

        [Test]
        public void Get_block_rlp_by_hash()
        {
            BlockDecoder decoder = new();
            Rlp rlp = decoder.Encode(Build.A.Block.WithNumber(1).TestObject);
            debugBridge.GetBlockRlp(Keccak.Zero).Returns(rlp.Bytes);

            DebugRpcModule rpcModule = new(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            JsonRpcSuccessResponse? response = RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getBlockRlpByHash", $"{Keccak.Zero.Bytes.ToHexString()}") as JsonRpcSuccessResponse;
            Assert.AreEqual(rlp.Bytes, (byte[]?)response?.Result);
        }

        [Test]
        public void Get_block_rlp()
        {
            BlockDecoder decoder = new();
            IDebugBridge debugBridge = Substitute.For<IDebugBridge>();
            Rlp rlp = decoder.Encode(Build.A.Block.WithNumber(1).TestObject);
            debugBridge.GetBlockRlp(1).Returns(rlp.Bytes);

            DebugRpcModule rpcModule = new(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            JsonRpcSuccessResponse? response = RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getBlockRlp", "1") as JsonRpcSuccessResponse;

            Assert.AreEqual(rlp.Bytes, (byte[]?)response?.Result);
        }

        [Test]
        public void Get_block_rlp_when_missing()
        {
            debugBridge.GetBlockRlp(1).Returns((byte[])null!);

            DebugRpcModule rpcModule = new(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            JsonRpcErrorResponse? response = RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getBlockRlp", "1") as JsonRpcErrorResponse;

            Assert.AreEqual(-32001, response?.Error?.Code);
        }

        [Test]
        public void Get_block_rlp_by_hash_when_missing()
        {
            BlockDecoder decoder = new();
            Rlp rlp = decoder.Encode(Build.A.Block.WithNumber(1).TestObject);
            debugBridge.GetBlockRlp(Keccak.Zero).Returns((byte[])null!);

            DebugRpcModule rpcModule = new(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            JsonRpcErrorResponse? response = RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getBlockRlpByHash", $"{Keccak.Zero.Bytes.ToHexString()}") as JsonRpcErrorResponse;

            Assert.AreEqual(-32001, response?.Error?.Code);
        }

        [Test]
        public void Get_trace()
        {
            GethTxTraceEntry entry = new();
            entry.Storage = new Dictionary<string, string>
            {
                {"1".PadLeft(64, '0'), "2".PadLeft(64, '0')},
                {"3".PadLeft(64, '0'), "4".PadLeft(64, '0')},
            };

            entry.Memory = new List<string>
            {
                "5".PadLeft(64, '0'),
                "6".PadLeft(64, '0')
            };

            entry.Stack = new List<string>
            {
                "7".PadLeft(64, '0'),
                "8".PadLeft(64, '0')
            };

            entry.Opcode = "STOP";
            entry.Gas = 22000;
            entry.GasCost = 1;
            entry.Depth = 1;

            var trace = new GethLikeTxTrace();
            trace.ReturnValue = Bytes.FromHexString("a2");
            trace.Entries.Add(entry);

            debugBridge.GetTransactionTrace(Arg.Any<Keccak>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>()).Returns(trace);

            DebugRpcModule rpcModule = new(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            string response = RpcTest.TestSerializedRequest<IDebugRpcModule>(DebugModuleFactory.Converters, rpcModule, "debug_traceTransaction", TestItem.KeccakA.ToString(true), "{}");

            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":{\"gas\":\"0x0\",\"failed\":false,\"returnValue\":\"0xa2\",\"structLogs\":[{\"pc\":0,\"op\":\"STOP\",\"gas\":22000,\"gasCost\":1,\"depth\":1,\"error\":null,\"stack\":[\"0000000000000000000000000000000000000000000000000000000000000007\",\"0000000000000000000000000000000000000000000000000000000000000008\"],\"memory\":[\"0000000000000000000000000000000000000000000000000000000000000005\",\"0000000000000000000000000000000000000000000000000000000000000006\"],\"storage\":{\"0000000000000000000000000000000000000000000000000000000000000001\":\"0000000000000000000000000000000000000000000000000000000000000002\",\"0000000000000000000000000000000000000000000000000000000000000003\":\"0000000000000000000000000000000000000000000000000000000000000004\"}}]},\"id\":67}", response);
        }

        [Test]
        public void Get_trace_with_options()
        {
            GethTxTraceEntry entry = new();
            entry.Storage = new Dictionary<string, string>
            {
                {"1".PadLeft(64, '0'), "2".PadLeft(64, '0')},
                {"3".PadLeft(64, '0'), "4".PadLeft(64, '0')},
            };

            entry.Memory = new List<string>
            {
                "5".PadLeft(64, '0'),
                "6".PadLeft(64, '0')
            };

            entry.Stack = new List<string>
            {
            };

            entry.Opcode = "STOP";
            entry.Gas = 22000;
            entry.GasCost = 1;
            entry.Depth = 1;

            var trace = new GethLikeTxTrace();
            trace.ReturnValue = Bytes.FromHexString("a2");
            trace.Entries.Add(entry);

            debugBridge.GetTransactionTrace(Arg.Any<Keccak>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>()).Returns(trace);

            DebugRpcModule rpcModule = new(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            string response = RpcTest.TestSerializedRequest<IDebugRpcModule>(DebugModuleFactory.Converters, rpcModule, "debug_traceTransaction", TestItem.KeccakA.ToString(true), "{disableStack : true}");

            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":{\"gas\":\"0x0\",\"failed\":false,\"returnValue\":\"0xa2\",\"structLogs\":[{\"pc\":0,\"op\":\"STOP\",\"gas\":22000,\"gasCost\":1,\"depth\":1,\"error\":null,\"stack\":[],\"memory\":[\"0000000000000000000000000000000000000000000000000000000000000005\",\"0000000000000000000000000000000000000000000000000000000000000006\"],\"storage\":{\"0000000000000000000000000000000000000000000000000000000000000001\":\"0000000000000000000000000000000000000000000000000000000000000002\",\"0000000000000000000000000000000000000000000000000000000000000003\":\"0000000000000000000000000000000000000000000000000000000000000004\"}}]},\"id\":67}", response);
        }

        [Test]
        public void Debug_traceCall_test()
        {
            GethTxTraceEntry entry = new();

            entry.Storage = new Dictionary<string, string>
            {
                {"1".PadLeft(64, '0'), "2".PadLeft(64, '0')},
                {"3".PadLeft(64, '0'), "4".PadLeft(64, '0')},
            };

            entry.Memory = new List<string>
            {
                "5".PadLeft(64, '0'),
                "6".PadLeft(64, '0')
            };

            entry.Stack = new List<string> { };
            entry.Opcode = "STOP";
            entry.Gas = 22000;
            entry.GasCost = 1;
            entry.Depth = 1;

            var trace = new GethLikeTxTrace();
            trace.ReturnValue = Bytes.FromHexString("a2");
            trace.Entries.Add(entry);

            GethTraceOptions gtOptions = new();

            Transaction transaction = Build.A.Transaction.WithTo(TestItem.AddressA).WithHash(TestItem.KeccakA).TestObject;
            TransactionForRpc txForRpc = new(transaction);

            debugBridge.GetTransactionTrace(Arg.Any<Transaction>(), Arg.Any<BlockParameter>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>()).Returns(trace);

            DebugRpcModule rpcModule = new(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            ResultWrapper<GethLikeTxTrace> debugTraceCall = rpcModule.debug_traceCall(txForRpc, null, gtOptions);
            ResultWrapper<GethLikeTxTrace> expected = ResultWrapper<GethLikeTxTrace>.Success(
                new GethLikeTxTrace()
                {
                    Failed = false,
                    Entries = new List<GethTxTraceEntry>()
                    {
                        new GethTxTraceEntry()
                        {
                            Gas = 22000,
                            GasCost = 1,
                            Depth = 1,
                            Memory = new List<string>()
                            {
                                "0000000000000000000000000000000000000000000000000000000000000005",
                                "0000000000000000000000000000000000000000000000000000000000000006"
                            },
                            Opcode = "STOP",
                            ProgramCounter = 0,
                            Stack = Array.Empty<string>(),
                            Storage = new Dictionary<string, string>()
                            {
                                {
                                    "0000000000000000000000000000000000000000000000000000000000000001",
                                    "0000000000000000000000000000000000000000000000000000000000000002"
                                },
                                {
                                    "0000000000000000000000000000000000000000000000000000000000000003",
                                    "0000000000000000000000000000000000000000000000000000000000000004"
                                },
                            }
                        }
                    },
                    Gas = 0,
                    ReturnValue = new byte[] { 162 }
                }
            );

            debugTraceCall.Should().BeEquivalentTo(expected);
        }

        [Test]
        public void Migrate_receipts()
        {
            debugBridge.MigrateReceipts(Arg.Any<long>()).Returns(true);
            IDebugRpcModule rpcModule = new DebugRpcModule(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            string response = RpcTest.TestSerializedRequest(rpcModule, "debug_migrateReceipts", "100");
            Assert.NotNull(response);
        }

        [Test]
        public void Update_head_block()
        {
            debugBridge.UpdateHeadBlock(Arg.Any<Keccak>());
            IDebugRpcModule rpcModule = new DebugRpcModule(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            RpcTest.TestSerializedRequest(rpcModule, "debug_resetHead", TestItem.KeccakA.ToString());
            debugBridge.Received().UpdateHeadBlock(TestItem.KeccakA);
        }

        [Test]
        public void TraceBlock_Success()
        {
            var traces = Enumerable.Repeat(MockGethLikeTrace(), 2).ToArray();
            var tracesClone = TestItem.CloneObject(traces);
            var blockRlp = new Rlp(TestItem.RandomDataA);

            debugBridge
                .GetBlockTrace(blockRlp, Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
                .Returns(traces);

            var rpcModule = new DebugRpcModule(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            var actual = rpcModule.debug_traceBlock(blockRlp.Bytes);
            var expected = ResultWrapper<GethLikeTxTrace[]>.Success(tracesClone);

            actual.Should().BeEquivalentTo(expected);
        }

        [Test]
        public void TraceBlock_Fail()
        {
            var blockRlp = new Rlp(TestItem.RandomDataA);

            debugBridge
                .GetBlockTrace(blockRlp, Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
                .Returns(default(GethLikeTxTrace[]));

            var rpcModule = new DebugRpcModule(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            var actual = rpcModule.debug_traceBlock(blockRlp.Bytes);
            var expected = ResultWrapper<GethLikeTxTrace[]>.Fail($"Trace is null for RLP {blockRlp.Bytes.ToHexString()}", ErrorCodes.ResourceNotFound);

            actual.Should().BeEquivalentTo(expected);
        }

        [Test]
        public void StandardTraceBlockToFile()
        {
            var blockHash = Keccak.EmptyTreeHash;

            static IEnumerable<string> GetFileNames(Keccak hash) =>
                new[] { $"block_{hash.ToShortString()}-0", $"block_{hash.ToShortString()}-1" };

            debugBridge
                .TraceBlockToFile(Arg.Is(blockHash), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
                .Returns(c => GetFileNames(c.ArgAt<Keccak>(0)));

            var rpcModule = new DebugRpcModule(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            var actual = rpcModule.debug_standardTraceBlockToFile(blockHash);
            var expected = ResultWrapper<IEnumerable<string>>.Success(GetFileNames(blockHash));

            actual.Should().BeEquivalentTo(expected);
        }

        [Test]
        public void TraceBlockByHash_Success()
        {
            var traces = Enumerable.Repeat(MockGethLikeTrace(), 2).ToArray();
            var tracesClone = TestItem.CloneObject(traces);
            var blockHash = TestItem.KeccakA;

            debugBridge
                .GetBlockTrace(new BlockParameter(blockHash), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
                .Returns(traces);

            var rpcModule = new DebugRpcModule(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            var actual = rpcModule.debug_traceBlockByHash(blockHash);
            var expected = ResultWrapper<GethLikeTxTrace[]>.Success(tracesClone);

            actual.Should().BeEquivalentTo(expected);
        }

        [Test]
        public void TraceBlockByHash_Fail()
        {
            var blockHash = TestItem.KeccakA;

            debugBridge
                .GetBlockTrace(new BlockParameter(blockHash), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
                .Returns(default(GethLikeTxTrace[]));

            var rpcModule = new DebugRpcModule(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            var actual = rpcModule.debug_traceBlockByHash(blockHash);
            var expected = ResultWrapper<GethLikeTxTrace[]>.Fail($"Trace is null for block {blockHash}", ErrorCodes.ResourceNotFound);

            actual.Should().BeEquivalentTo(expected);
        }

        [Test]
        public void TraceBlockByNumber_Success()
        {
            var traces = Enumerable.Repeat(MockGethLikeTrace(), 2).ToArray();
            var tracesClone = TestItem.CloneObject(traces);
            var blockNumber = BlockParameter.Latest;

            debugBridge
                .GetBlockTrace(blockNumber, Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
                .Returns(traces);

            var rpcModule = new DebugRpcModule(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            var actual = rpcModule.debug_traceBlockByNumber(blockNumber);
            var expected = ResultWrapper<GethLikeTxTrace[]>.Success(tracesClone);

            actual.Should().BeEquivalentTo(expected);
        }

        [Test]
        public void TraceBlockByNumber_Fail()
        {
            var blockNumber = BlockParameter.Latest;

            debugBridge
                .GetBlockTrace(blockNumber, Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
                .Returns(default(GethLikeTxTrace[]));

            var rpcModule = new DebugRpcModule(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            var actual = rpcModule.debug_traceBlockByNumber(blockNumber);
            var expected = ResultWrapper<GethLikeTxTrace[]>.Fail($"Trace is null for block {blockNumber}", ErrorCodes.ResourceNotFound);

            actual.Should().BeEquivalentTo(expected);
        }

        private static GethLikeTxTrace MockGethLikeTrace()
        {
            var trace = new GethLikeTxTrace { ReturnValue = new byte[] { 0xA2 } };

            trace.Entries.Add(new GethTxTraceEntry
            {
                Depth = 1,
                Gas = 22000,
                GasCost = 1,
                Memory = new List<string>
                {
                    "5".PadLeft(64, '0'),
                    "6".PadLeft(64, '0')
                },
                Opcode = "STOP",
                ProgramCounter = 32,
                Stack = new List<string>
                {
                    "7".PadLeft(64, '0'),
                    "8".PadLeft(64, '0')
                },
                Storage = new Dictionary<string, string>
                {
                    {"1".PadLeft(64, '0'), "2".PadLeft(64, '0')},
                    {"3".PadLeft(64, '0'), "4".PadLeft(64, '0')},
                }
            });

            return trace;
        }
    }
}
