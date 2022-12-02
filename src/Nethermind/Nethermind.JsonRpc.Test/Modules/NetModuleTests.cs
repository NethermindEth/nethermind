// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.Logging;
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
            Enode enode = new(TestItem.PublicKeyA, IPAddress.Loopback, 30303);
            NetBridge netBridge = new(enode, Substitute.For<ISyncServer>());
            NetRpcModule rpcModule = new(LimboLogs.Instance, netBridge);
            string response = RpcTest.TestSerializedRequest<INetRpcModule>(rpcModule, "net_peerCount");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x0\",\"id\":67}", response);
        }

        [Test]
        public void NetVersionSuccessTest()
        {
            Enode enode = new(TestItem.PublicKeyA, IPAddress.Loopback, 30303);
            NetBridge netBridge = new(enode, Substitute.For<ISyncServer>());
            NetRpcModule rpcModule = new(LimboLogs.Instance, netBridge);
            string response = RpcTest.TestSerializedRequest<INetRpcModule>(rpcModule, "net_version");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0\",\"id\":67}", response);
        }

        [Test]
        public void NetListeningSuccessTest()
        {
            Enode enode = new(TestItem.PublicKeyA, IPAddress.Loopback, 30303);
            NetBridge netBridge = new(enode, Substitute.For<ISyncServer>());
            NetRpcModule rpcModule = new(LimboLogs.Instance, netBridge);
            string response = RpcTest.TestSerializedRequest<INetRpcModule>(rpcModule, "net_listening");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}", response);
        }
    }
}
