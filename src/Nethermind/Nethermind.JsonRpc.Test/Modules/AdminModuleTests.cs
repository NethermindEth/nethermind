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

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Serialization.Json;
using Nethermind.Stats.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class AdminModuleTests
    {
        private IAdminModule _adminModule;
        private EthereumJsonSerializer _serializer;
        private NetworkConfig _networkConfig;
        private IBlockTree _blockTree;
        private const string _enodeString = "enode://44826a5d6a55f88a18298bca4773fca5749cdc3a5c9f308aa7d810e9b31123f3e7c5fba0b1d70aac5308426f47df2a128a6747040a3815cc7dd7167d03be320d@127.0.0.1:30303";
        private const string _exampleDataDir = "/example/dbdir";
        
        [SetUp]
        public void Setup()
        {
            _blockTree = Build.A.BlockTree().OfChainLength(5).TestObject;
            _networkConfig = new NetworkConfig();
            IPeerManager peerManager = Substitute.For<IPeerManager>();
            peerManager.ActivePeers.Returns(new List<Peer> {new Peer(new Node("127.0.0.1", 30303, true))});
            
            IStaticNodesManager staticNodesManager = Substitute.For<IStaticNodesManager>();
            Enode enode = new Enode(_enodeString);
            _adminModule = new AdminModule(_blockTree, _networkConfig, peerManager, staticNodesManager, enode, _exampleDataDir);
            _serializer = new EthereumJsonSerializer();
        }
        
        [Test]
        public void Test_node_info()
        {
            string serialized = RpcTest.TestSerializedRequest(_adminModule, "admin_nodeInfo");
            JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
            JsonSerializerSettings settings = new JsonSerializerSettings();
            settings.Converters = EthereumJsonSerializer.CommonConverters.ToList();
            
            NodeInfo nodeInfo = ((JObject) response.Result).ToObject<NodeInfo>(JsonSerializer.Create(settings));
            nodeInfo.Enode.Should().Be(_enodeString);
            nodeInfo.Id.Should().Be("44826a5d6a55f88a18298bca4773fca5749cdc3a5c9f308aa7d810e9b31123f3e7c5fba0b1d70aac5308426f47df2a128a6747040a3815cc7dd7167d03be320d");
            nodeInfo.Ip.Should().Be("127.0.0.1");
            nodeInfo.Name.Should().Be(ClientVersion.Description);
            nodeInfo.ListenAddress.Should().Be("127.0.0.1:30303");
            nodeInfo.Ports.Discovery.Should().Be(_networkConfig.DiscoveryPort);
            nodeInfo.Ports.Listener.Should().Be(_networkConfig.P2PPort);

            nodeInfo.Protocols.Should().HaveCount(1);
            nodeInfo.Protocols["eth"].Difficulty.Should().Be(_blockTree.Head.TotalDifficulty ?? 0);
            nodeInfo.Protocols["eth"].HeadHash.Should().Be(_blockTree.HeadHash);
            nodeInfo.Protocols["eth"].GenesisHash.Should().Be(_blockTree.GenesisHash);
            nodeInfo.Protocols["eth"].ChainId.Should().Be(_blockTree.ChainId);
        }
        
        [Test]
        public void Test_data_dir()
        {
            string serialized = RpcTest.TestSerializedRequest(_adminModule, "admin_dataDir");
            JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
            response.Result.Should().Be(_exampleDataDir);
        }
        
        [Test]
        public void Smoke_solc()
        {
            string serialized = RpcTest.TestSerializedRequest(_adminModule, "admin_setSolc");
        }
        
        [Test]
        public void Smoke_test_peers()
        {
            string serialized0 = RpcTest.TestSerializedRequest(_adminModule, "admin_addPeer", _enodeString);
            string serialized1 = RpcTest.TestSerializedRequest(_adminModule, "admin_removePeer", _enodeString);
            string serialized2 = RpcTest.TestSerializedRequest(_adminModule, "admin_addPeer", _enodeString, "true");
            string serialized3 = RpcTest.TestSerializedRequest(_adminModule, "admin_removePeer", _enodeString, "true");
            string serialized4 = RpcTest.TestSerializedRequest(_adminModule, "admin_peers");
        }
    }
}