// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
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
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Stats.Model;
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
        ConcurrentDictionary<PublicKeyAsKey, Peer> dict = new();
        dict.TryAdd(TestItem.PublicKeyA, new Peer(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303, true)));
        peerPool.ActivePeers.Returns(dict);

        IStaticNodesManager staticNodesManager = Substitute.For<IStaticNodesManager>();
        Enode enode = new(_enodeString);
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters()
        };

        _adminRpcModule = new AdminRpcModule(
            _blockTree,
            _networkConfig,
            peerPool,
            staticNodesManager,
            enode,
            _exampleDataDir,
            new ManualPruningTrigger(),
            chainSpec.Parameters);

        _serializer = new EthereumJsonSerializer();
    }

    [Test]
    public async Task Test_peers()
    {
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_peers");
        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        var peerInfoList = ((JsonElement)response.Result!).Deserialize<List<PeerInfo>>(EthereumJsonSerializer.JsonOptions)!;
        peerInfoList.Count.Should().Be(1);
        PeerInfo peerInfo = peerInfoList[0];
        peerInfo.Host.Should().Be("127.0.0.1");
        peerInfo.Port.Should().Be(30303);
        peerInfo.Inbound.Should().BeFalse();
        peerInfo.IsStatic.Should().BeTrue();
        peerInfo.Id.Should().NotBeEmpty();
    }

    [Test]
    public async Task Test_node_info()
    {
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_nodeInfo");
        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        NodeInfo nodeInfo = ((JsonElement)response.Result!).Deserialize<NodeInfo>(EthereumJsonSerializer.JsonOptions)!;
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
        nodeInfo.Protocols["eth"].NewtorkId.Should().Be(_blockTree.NetworkId);
        nodeInfo.Protocols["eth"].ChainId.Should().Be(_blockTree.ChainId);
    }

    [Test]
    public async Task Test_admin_dataDir()
    {
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_dataDir");
        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        response.Result!.ToString().Should().Be(_exampleDataDir);
    }

    [Test]
    public async Task Smoke_solc()
    {
        _ = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_setSolc");
    }

    [Test]
    public async Task Smoke_test_peers()
    {
        _ = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_addPeer", _enodeString);
        _ = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_removePeer", _enodeString);
        _ = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_addPeer", _enodeString, true);
        _ = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_removePeer", _enodeString, true);
        _ = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_peers");
    }
}
