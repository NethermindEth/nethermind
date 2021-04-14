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

using System.Collections.Generic;
using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
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
            byte[] key = new byte[] {1, 2, 3};
            byte[] value = new byte[] {4, 5, 6};
            debugBridge.GetDbValue(Arg.Any<string>(), Arg.Any<byte[]>()).Returns(value);

            IConfigProvider configProvider = Substitute.For<IConfigProvider>();
            DebugRpcModule rpcModule = new DebugRpcModule(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            JsonRpcSuccessResponse response = RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getFromDb", "STATE", key.ToHexString(true)) as JsonRpcSuccessResponse;

            byte[] result = response.Result as byte[];
        }

        [Test]
        public void Get_from_db_null_value()
        {
            byte[] key = new byte[] {1, 2, 3};
            debugBridge.GetDbValue(Arg.Any<string>(), Arg.Any<byte[]>()).Returns((byte[]) null);

            IConfigProvider configProvider = Substitute.For<IConfigProvider>();
            DebugRpcModule rpcModule = new DebugRpcModule(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            JsonRpcSuccessResponse response = RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getFromDb", "STATE", key.ToHexString(true)) as JsonRpcSuccessResponse;

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

            DebugRpcModule rpcModule = new DebugRpcModule(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            JsonRpcSuccessResponse response = RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getChainLevel", parameter) as JsonRpcSuccessResponse;
            ChainLevelForRpc chainLevel = response?.Result as ChainLevelForRpc;
            Assert.NotNull(chainLevel);
            Assert.AreEqual(true, chainLevel.HasBlockOnMainChain);
            Assert.AreEqual(2, chainLevel.BlockInfos.Length);
        }

        [Test]
        public void Get_block_rlp_by_hash()
        {
            BlockDecoder decoder = new BlockDecoder();
            Rlp rlp = decoder.Encode(Build.A.Block.WithNumber(1).TestObject);
            debugBridge.GetBlockRlp(Keccak.Zero).Returns(rlp.Bytes);

            DebugRpcModule rpcModule = new DebugRpcModule(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            JsonRpcSuccessResponse response = RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getBlockRlpByHash", $"{Keccak.Zero.Bytes.ToHexString()}") as JsonRpcSuccessResponse;
            Assert.AreEqual(rlp.Bytes, (byte[]) response?.Result);
        }

        [Test]
        public void Get_block_rlp()
        {
            BlockDecoder decoder = new BlockDecoder();
            IDebugBridge debugBridge = Substitute.For<IDebugBridge>();
            Rlp rlp = decoder.Encode(Build.A.Block.WithNumber(1).TestObject);
            debugBridge.GetBlockRlp(1).Returns(rlp.Bytes);

            DebugRpcModule rpcModule = new DebugRpcModule(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            JsonRpcSuccessResponse response = RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getBlockRlp", "1") as JsonRpcSuccessResponse;

            Assert.AreEqual(rlp.Bytes, (byte[]) response?.Result);
        }

        [Test]
        public void Get_block_rlp_when_missing()
        {
            debugBridge.GetBlockRlp(1).Returns((byte[]) null);

            DebugRpcModule rpcModule = new DebugRpcModule(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            JsonRpcErrorResponse response = RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getBlockRlp", "1") as JsonRpcErrorResponse;

            Assert.AreEqual(-32001, response?.Error.Code);
        }

        [Test]
        public void Get_block_rlp_by_hash_when_missing()
        {
            BlockDecoder decoder = new BlockDecoder();
            Rlp rlp = decoder.Encode(Build.A.Block.WithNumber(1).TestObject);
            debugBridge.GetBlockRlp(Keccak.Zero).Returns((byte[]) null);

            DebugRpcModule rpcModule = new DebugRpcModule(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            JsonRpcErrorResponse response = RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getBlockRlpByHash", $"{Keccak.Zero.Bytes.ToHexString()}") as JsonRpcErrorResponse;

            Assert.AreEqual(-32001, response.Error.Code);
        }

        [Test]
        public void Get_trace()
        {
            GethTxTraceEntry entry = new GethTxTraceEntry();
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

            entry.Operation = "STOP";
            entry.Gas = 22000;
            entry.GasCost = 1;
            entry.Depth = 1;

            var trace = new GethLikeTxTrace();
            trace.ReturnValue = Bytes.FromHexString("a2");
            trace.Entries.Add(entry);

            debugBridge.GetTransactionTrace(Arg.Any<Keccak>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>()).Returns(trace);

            DebugRpcModule rpcModule = new DebugRpcModule(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            string response = RpcTest.TestSerializedRequest<IDebugRpcModule>(DebugModuleFactory.Converters, rpcModule, "debug_traceTransaction", TestItem.KeccakA.ToString(true), "{}");

            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":{\"gas\":\"0x0\",\"failed\":false,\"returnValue\":\"0xa2\",\"structLogs\":[{\"pc\":0,\"op\":\"STOP\",\"gas\":22000,\"gasCost\":1,\"depth\":1,\"error\":null,\"stack\":[\"0000000000000000000000000000000000000000000000000000000000000007\",\"0000000000000000000000000000000000000000000000000000000000000008\"],\"memory\":[\"0000000000000000000000000000000000000000000000000000000000000005\",\"0000000000000000000000000000000000000000000000000000000000000006\"],\"storage\":{\"0000000000000000000000000000000000000000000000000000000000000001\":\"0000000000000000000000000000000000000000000000000000000000000002\",\"0000000000000000000000000000000000000000000000000000000000000003\":\"0000000000000000000000000000000000000000000000000000000000000004\"}}]},\"id\":67}", response);
        }

        [Test]
        public void Get_trace_with_options()
        {
            GethTxTraceEntry entry = new GethTxTraceEntry();
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

            entry.Operation = "STOP";
            entry.Gas = 22000;
            entry.GasCost = 1;
            entry.Depth = 1;

            var trace = new GethLikeTxTrace();
            trace.ReturnValue = Bytes.FromHexString("a2");
            trace.Entries.Add(entry);

            debugBridge.GetTransactionTrace(Arg.Any<Keccak>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>()).Returns(trace);

            DebugRpcModule rpcModule = new DebugRpcModule(LimboLogs.Instance, debugBridge, jsonRpcConfig);
            string response = RpcTest.TestSerializedRequest<IDebugRpcModule>(DebugModuleFactory.Converters, rpcModule, "debug_traceTransaction", TestItem.KeccakA.ToString(true), "{disableStack : true}");

            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":{\"gas\":\"0x0\",\"failed\":false,\"returnValue\":\"0xa2\",\"structLogs\":[{\"pc\":0,\"op\":\"STOP\",\"gas\":22000,\"gasCost\":1,\"depth\":1,\"error\":null,\"stack\":[],\"memory\":[\"0000000000000000000000000000000000000000000000000000000000000005\",\"0000000000000000000000000000000000000000000000000000000000000006\"],\"storage\":{\"0000000000000000000000000000000000000000000000000000000000000001\":\"0000000000000000000000000000000000000000000000000000000000000002\",\"0000000000000000000000000000000000000000000000000000000000000003\":\"0000000000000000000000000000000000000000000000000000000000000004\"}}]},\"id\":67}", response);
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
    }
}
