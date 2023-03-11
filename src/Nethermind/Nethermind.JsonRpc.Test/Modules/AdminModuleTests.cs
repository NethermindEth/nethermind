// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class AdminModuleTests
{
    private IAdminRpcModule _adminRpcModule = null!;
    private EthereumJsonSerializer _serializer = null!;
    private NetworkConfig _networkConfig = null!;
    private IBlockTree _blockTree = null!;
    private const string _enodeString = "enode://e1b7e0dc09aae610c9dec8a0bee62bab9946cc27ebdd2f9e3571ed6d444628f99e91e43f4a14d42d498217608bb3e1d1bc8ec2aa27d7f7e423413b851bae02bc@127.0.0.1:30303";
    private const string _exampleDataDir = "/example/dbdir";

    [SetUp]
    public void Setup()
    {
        _blockTree = Build.A.BlockTree().OfChainLength(5).TestObject;
        _networkConfig = new NetworkConfig();
        IPeerPool peerPool = Substitute.For<IPeerPool>();
        ConcurrentDictionary<PublicKey, Peer> dict = new();
        dict.TryAdd(TestItem.PublicKeyA, new Peer(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303, true)));
        peerPool.ActivePeers.Returns(dict);

        IStaticNodesManager staticNodesManager = Substitute.For<IStaticNodesManager>();
        Enode enode = new(_enodeString);
        _adminRpcModule = new AdminRpcModule(
            _blockTree,
            _networkConfig,
            peerPool,
            staticNodesManager,
            enode,
            _exampleDataDir,
            new ManualPruningTrigger());

        _serializer = new EthereumJsonSerializer();
    }

    [Test]
    public void Test_node_info()
    {
        string serialized = RpcTest.TestSerializedRequest(_adminRpcModule, "admin_nodeInfo");
        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        JsonSerializerSettings settings = new();
        settings.Converters = EthereumJsonSerializer.CommonConverters.ToList();

        NodeInfo nodeInfo = ((JObject)response.Result!).ToObject<NodeInfo>(JsonSerializer.Create(settings))!;
        nodeInfo.Enode.Should().Be(_enodeString);
        nodeInfo.Id.Should().Be("ae3623ef35c06ab49e9ae4b9f5a2b0f1983c28f85de1ccc98e2174333fdbdf1f");
        nodeInfo.Ip.Should().Be("127.0.0.1");
        nodeInfo.Name.Should().Be(ProductInfo.ClientId);
        nodeInfo.ListenAddress.Should().Be("127.0.0.1:30303");
        nodeInfo.Ports.Discovery.Should().Be(_networkConfig.DiscoveryPort);
        nodeInfo.Ports.Listener.Should().Be(_networkConfig.P2PPort);

        nodeInfo.Protocols.Should().HaveCount(1);
        nodeInfo.Protocols["eth"].Difficulty.Should().Be(_blockTree.Head?.TotalDifficulty ?? 0);
        nodeInfo.Protocols["eth"].HeadHash.Should().Be(_blockTree.HeadHash);
        nodeInfo.Protocols["eth"].GenesisHash.Should().Be(_blockTree.GenesisHash);
        nodeInfo.Protocols["eth"].ChainId.Should().Be(_blockTree.ChainId);
    }

    [Test]
    public void Test_data_dir()
    {
        string serialized = RpcTest.TestSerializedRequest(_adminRpcModule, "admin_dataDir");
        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        response.Result.Should().Be(_exampleDataDir);
    }

    [Test]
    public void Smoke_solc()
    {
        string unused = RpcTest.TestSerializedRequest(_adminRpcModule, "admin_setSolc");
    }

    [Test]
    public void Smoke_test_peers()
    {
        string unused0 = RpcTest.TestSerializedRequest(_adminRpcModule, "admin_addPeer", _enodeString);
        string unused1 = RpcTest.TestSerializedRequest(_adminRpcModule, "admin_removePeer", _enodeString);
        string unused2 = RpcTest.TestSerializedRequest(_adminRpcModule, "admin_addPeer", _enodeString, "true");
        string unused3 = RpcTest.TestSerializedRequest(_adminRpcModule, "admin_removePeer", _enodeString, "true");
        string unused4 = RpcTest.TestSerializedRequest(_adminRpcModule, "admin_peers");
    }
}
