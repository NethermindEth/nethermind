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
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.Module;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test
{
    [TestFixture]
    public class EthModuleTests
    {
        [SetUp]
        public void Initialize()
        {
        }

        [Test]
        public void Eth_get_balance()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.FindBlock(Arg.Any<UInt256>()).Returns(Build.A.Block.TestObject);
            bridge.GetAccount(Arg.Any<Address>(), Arg.Any<Keccak>()).Returns(Build.A.Account.WithBalance(1.Ether()).TestObject);
            bridge.Head.Returns(Build.A.BlockHeader.TestObject);
            
            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), new JsonRpcModelMapper(), NullLogManager.Instance, bridge);
            
            string serialized = RpcTest.TestSerializedRequest(module, "eth_getBalance", TestObject.AddressA.Bytes.ToHexString(true), "0x01");
            
            Assert.AreEqual(serialized, "{\"id\":67,\"jsonrpc\":\"\",\"result\":\"0x0de0b6b3a7640000\"}");
        }
        
        [Test]
        public void Eth_get_failure()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.Head.Returns((BlockHeader)null);
            
            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), new JsonRpcModelMapper(), NullLogManager.Instance, bridge);
            
            string serialized = RpcTest.TestSerializedRequest(module, "eth_getBalance", TestObject.AddressA.Bytes.ToHexString(true), "0x01");
            
            Assert.AreEqual(serialized, "{\"id\":67,\"jsonrpc\":\"\",\"result\":null,\"error\":{\"code\":0,\"message\":\"Internal error\",\"data\":null}}");
        }
    }
}