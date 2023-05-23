// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
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
            Assert.That(response, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x0\",\"id\":67}"));
        }

        [Test]
        public void NetVersionSuccessTest()
        {
            Enode enode = new(TestItem.PublicKeyA, IPAddress.Loopback, 30303);
            var blockTree = Substitute.For<IBlockTree>();
            blockTree.NetworkId.Returns((ulong)TestBlockchainIds.NetworkId);
            blockTree.ChainId.Returns((ulong)TestBlockchainIds.ChainId);
            var syncConfig = Substitute.For<ISyncConfig>();
            syncConfig.PivotHash.Returns(Keccak.MaxValue.ToString());
            ISyncServer syncServer = new SyncServer(
                Substitute.For<IReadOnlyKeyValueStore>(),
                Substitute.For<IReadOnlyKeyValueStore>(),
                blockTree,
                Substitute.For<IReceiptFinder>(),
                Substitute.For<IBlockValidator>(),
                Substitute.For<ISealValidator>(),
                Substitute.For<ISyncPeerPool>(),
                Substitute.For<ISyncModeSelector>(),
                syncConfig,
                Substitute.For<IWitnessRepository>(),
                Substitute.For<IGossipPolicy>(),
                Substitute.For<ISpecProvider>(),
                Substitute.For<ILogManager>());
            NetBridge netBridge = new(enode, syncServer);
            NetRpcModule rpcModule = new(LimboLogs.Instance, netBridge);
            string response = RpcTest.TestSerializedRequest<INetRpcModule>(rpcModule, "net_version");
            Assert.That(response, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{TestBlockchainIds.NetworkId}\",\"id\":67}}"));

            _ = blockTree.DidNotReceive().ChainId;
            _ = blockTree.Received().NetworkId;
        }

        [Test]
        public void NetListeningSuccessTest()
        {
            Enode enode = new(TestItem.PublicKeyA, IPAddress.Loopback, 30303);
            NetBridge netBridge = new(enode, Substitute.For<ISyncServer>());
            NetRpcModule rpcModule = new(LimboLogs.Instance, netBridge);
            string response = RpcTest.TestSerializedRequest<INetRpcModule>(rpcModule, "net_listening");
            Assert.That(response, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}"));
        }
    }
}
