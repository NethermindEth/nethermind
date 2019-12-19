//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.Logging;
using Nethermind.Network;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    public class NetModuleTests
    {
        private INetModule _netModule;

        [SetUp]
        public void Initialize()
        {
            _netModule = new NetModule(NullLogManager.Instance, Substitute.For<INetBridge>());
        }

        [Test]
        public void NetPeerCountSuccessTest()
        {
            Enode enode = new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303);
            NetBridge netBridge = new NetBridge(enode, Substitute.For<ISyncServer>(), Substitute.For<IPeerManager>());
            NetModule module = new NetModule(NullLogManager.Instance, netBridge);
            string response = RpcTest.TestSerializedRequest<INetModule>(module, "net_peerCount");
            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0x0\"}", response);
        }
        
        [Test]
        public void NetVersionSuccessTest()
        {
            Enode enode = new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303);
            NetBridge netBridge = new NetBridge(enode, Substitute.For<ISyncServer>(), Substitute.For<IPeerManager>());
            NetModule module = new NetModule(NullLogManager.Instance, netBridge);
            string response = RpcTest.TestSerializedRequest<INetModule>(module, "net_version");
            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0\"}", response);
        }
    }
}