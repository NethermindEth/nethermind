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

using Nethermind.Blockchain.Filters;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.Eth;
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

            Assert.AreEqual(serialized, "{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0xde0b6b3a7640000\"}");
        }

        [Test]
        public void Eth_get_balance_internal_error()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.Head.Returns((BlockHeader) null);

            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), new JsonRpcModelMapper(), NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(module, "eth_getBalance", TestObject.AddressA.Bytes.ToHexString(true), "0x01");

            Assert.AreEqual(serialized, "{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":null,\"error\":{\"code\":-32603,\"message\":\"Internal error\",\"data\":null}}");
        }

        [Test]
        public void Eth_get_balance_incorrect_number_of_params()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.Head.Returns((BlockHeader) null);

            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), new JsonRpcModelMapper(), NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(module, "eth_getBalance", TestObject.AddressA.Bytes.ToHexString(true));

            Assert.AreEqual(serialized, "{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":null,\"error\":{\"code\":-32602,\"message\":\"Incorrect parameters count, expected: 2, actual: 1\",\"data\":null}}");
        }

        [Test]
        public void Eth_get_balance_incorrect_parameters()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.Head.Returns((BlockHeader) null);

            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), new JsonRpcModelMapper(), NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(module, "eth_getBalance", TestObject.KeccakA.Bytes.ToHexString(true), "0x01");

            Assert.AreEqual(serialized, "{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":null,\"error\":{\"code\":-32602,\"message\":\"Incorrect parameters\",\"data\":null}}");
        }

        [Test]
        public void Eth_syncing_true()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.IsSyncing.Returns(false);
            bridge.Head.Returns(Build.A.BlockHeader.WithNumber(900).TestObject);
            bridge.BestKnown.Returns((UInt256) 1000);

            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), new JsonRpcModelMapper(), NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(module, "eth_syncing");

            Assert.AreEqual(serialized, "{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":{\"startingBlock\":\"0x0\",\"currentBlock\":\"0x384\",\"highestBlock\":\"0x3e8\"}}");
        }

        [Test]
        [Ignore("We always return true")]
        [Todo(Improve.MissingFunctionality, "Add correct reporting of not syncing")]
        public void Eth_syncing_false()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.IsSyncing.Returns(true);
            bridge.Head.Returns(Build.A.BlockHeader.WithNumber(900).TestObject);
            bridge.BestKnown.Returns((UInt256) 1000);

            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), new JsonRpcModelMapper(), NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(module, "eth_syncing");

            Assert.AreEqual(serialized, "{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":false}");
        }

        [Test]
        public void Eth_get_filter_logs()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.GetFilterLogs(Arg.Any<int>()).Returns(new [] {new FilterLog(1, 1, TestObject.KeccakA, 1, TestObject.KeccakB, TestObject.AddressA, new byte[] {1, 2, 3}, new [] {TestObject.KeccakC, TestObject.KeccakD})});
            bridge.FilterExists(1).Returns(true);

            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), new JsonRpcModelMapper(), NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(module, "eth_getFilterLogs", "0x01");

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":[{\"removed\":false,\"logIndex\":\"0x1\",\"blockNumber\":\"0x1\",\"blockHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"transactionHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"transactionIndex\":\"0x1\",\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"data\":\"0x010203\",\"topics\":[\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"0x6c3fd336b49dcb1c57dd4fbeaf5f898320b0da06a5ef64e798c6497600bb79f2\"]}]}", serialized);
        }
    }
}