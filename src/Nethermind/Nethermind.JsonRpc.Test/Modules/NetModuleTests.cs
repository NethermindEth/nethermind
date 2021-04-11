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

using System.Net;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Synchronization;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NetModuleTests
    {
        [Test]
        public void NetPeerCountSuccessTest()
        {
            Enode enode = new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303);
            NetBridge netBridge = new NetBridge(enode, Substitute.For<ISyncServer>());
            NetRpcModule rpcModule = new NetRpcModule(LimboLogs.Instance, netBridge);
            string response = RpcTest.TestSerializedRequest<INetRpcModule>(rpcModule, "net_peerCount");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x0\",\"id\":67}", response);
        }

        [Test]
        public void NetVersionSuccessTest()
        {
            Enode enode = new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303);
            NetBridge netBridge = new NetBridge(enode, Substitute.For<ISyncServer>());
            NetRpcModule rpcModule = new NetRpcModule(LimboLogs.Instance, netBridge);
            string response = RpcTest.TestSerializedRequest<INetRpcModule>(rpcModule, "net_version");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0\",\"id\":67}", response);
        }

        [Test]
        public void NetListeningSuccessTest()
        {
            Enode enode = new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303);
            NetBridge netBridge = new NetBridge(enode, Substitute.For<ISyncServer>());
            NetRpcModule rpcModule = new NetRpcModule(LimboLogs.Instance, netBridge);
            string response = RpcTest.TestSerializedRequest<INetRpcModule>(rpcModule, "net_listening");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}", response);
        }
    }
}
