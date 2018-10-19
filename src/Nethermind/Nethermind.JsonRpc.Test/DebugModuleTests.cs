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

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.JsonRpc.DataModel;
using Nethermind.JsonRpc.Module;
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test
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
            IJsonRpcModelMapper modelMapper = new JsonRpcModelMapper();
            DebugModule module = new DebugModule(configProvider, NullLogManager.Instance, debugBridge, modelMapper, new UnforgivingJsonSerializer());
            JsonRpcResponse response = RpcTest.TestRequest<IDebugModule>(module, "debug_getFromDb", "STATE", key.ToHexString());
            
            byte[] result = Bytes.FromHexString((string)response.Result);
            Assert.AreEqual(value, result);
        }

        [Test]
        public void Get_from_db_null_value()
        {   
            IDebugBridge debugBridge = Substitute.For<IDebugBridge>();            
            byte[] key = new byte[] {1, 2, 3};
            debugBridge.GetDbValue(Arg.Any<string>(), Arg.Any<byte[]>()).Returns((byte[])null);

            IConfigProvider configProvider = Substitute.For<IConfigProvider>();
            IJsonRpcModelMapper modelMapper = new JsonRpcModelMapper();
            DebugModule module = new DebugModule(configProvider, NullLogManager.Instance, debugBridge, modelMapper, new UnforgivingJsonSerializer());
            JsonRpcResponse response = RpcTest.TestRequest<IDebugModule>(module, "debug_getFromDb", "STATE", key.ToHexString());
            
            Assert.IsNull(response.Error, "error");
            Assert.IsNull(response.Result, "result");
        }
    }
}