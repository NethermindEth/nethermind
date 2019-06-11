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

using System.Collections.Generic;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Json;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    public class DebugModuleTests
    {
        [Test]
        public void Get_from_db()
        {
            IDebugBridge debugBridge = Substitute.For<IDebugBridge>();
            byte[] key = new byte[] {1, 2, 3};
            byte[] value = new byte[] {4, 5, 6};
            debugBridge.GetDbValue(Arg.Any<string>(), Arg.Any<byte[]>()).Returns(value);
            
            IConfigProvider configProvider = Substitute.For<IConfigProvider>();
            DebugModule module = new DebugModule(NullLogManager.Instance, debugBridge);
            JsonRpcResponse response = RpcTest.TestRequest<IDebugModule>(module, "debug_getFromDb", "STATE", key.ToHexString(true));
            
            byte[] result = response.Result as byte[];
            Assert.AreEqual(value, result, response.Error?.Message);
        }

        [Test]
        public void Get_from_db_null_value()
        {   
            IDebugBridge debugBridge = Substitute.For<IDebugBridge>();            
            byte[] key = new byte[] {1, 2, 3};
            debugBridge.GetDbValue(Arg.Any<string>(), Arg.Any<byte[]>()).Returns((byte[])null);

            IConfigProvider configProvider = Substitute.For<IConfigProvider>();
            DebugModule module = new DebugModule(NullLogManager.Instance, debugBridge);
            JsonRpcResponse response = RpcTest.TestRequest<IDebugModule>(module, "debug_getFromDb", "STATE", key.ToHexString(true));
            
            Assert.IsNull(response.Error, response.Error?.Message);
            Assert.IsNull(response.Result, "result");
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
            
            IDebugBridge debugBridge = Substitute.For<IDebugBridge>();            
            debugBridge.GetTransactionTrace(Arg.Any<Keccak>()).Returns(trace);

            IConfigProvider configProvider = Substitute.For<IConfigProvider>();
            DebugModule module = new DebugModule(NullLogManager.Instance, debugBridge);
            string response = RpcTest.TestSerializedRequest<IDebugModule>(module, "debug_traceTransaction", TestItem.KeccakA.ToString(true));
            
            Assert.AreEqual("{\"id\":\"0x43\",\"jsonrpc\":\"2.0\",\"result\":{\"gas\":\"0x0\",\"failed\":false,\"returnValue\":\"0xa2\",\"structLogs\":[{\"pc\":0,\"op\":\"STOP\",\"gas\":22000,\"gasCost\":1,\"depth\":1,\"error\":null,\"stack\":[\"0000000000000000000000000000000000000000000000000000000000000007\",\"0000000000000000000000000000000000000000000000000000000000000008\"],\"memory\":[\"0000000000000000000000000000000000000000000000000000000000000005\",\"0000000000000000000000000000000000000000000000000000000000000006\"],\"storage\":{\"0000000000000000000000000000000000000000000000000000000000000001\":\"0000000000000000000000000000000000000000000000000000000000000002\",\"0000000000000000000000000000000000000000000000000000000000000003\":\"0000000000000000000000000000000000000000000000000000000000000004\"}}]}}", response);
        }
    }
}