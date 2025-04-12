// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.Self)]
public class NodesLoaderTests
{
    private NetworkConfig _networkConfig;
    private DiscoveryConfig _discoveryConfig;
    private INodeStatsManager _statsManager;
    private INetworkStorage _peerStorage;
    private NodesLoader _loader;

    [SetUp]
    public void SetUp()
    {
        _networkConfig = new NetworkConfig();
        _discoveryConfig = new DiscoveryConfig();
        _statsManager = Substitute.For<INodeStatsManager>();
        _peerStorage = Substitute.For<INetworkStorage>();
        IRlpxHost rlpxHost = Substitute.For<IRlpxHost>();
        _loader = new NodesLoader(_networkConfig, _statsManager, _peerStorage, rlpxHost, LimboLogs.Instance);
    }

    [Test]
    public void When_no_peers_then_no_peers_nada_zero()
    {
        List<Node> peers = _loader.DiscoverNodes(default).ToBlockingEnumerable().ToList();
        Assert.That(peers.Count, Is.EqualTo(0));
    }

    private const string enodesString = enode1String + "," + enode2String;
    private const string enode1String = "enode://22222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222@51.141.78.53:30303";
    private const string enode2String = "enode://1111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111b@52.141.78.53:30303";

    [Test]
    public void Can_load_static_nodes()
    {
        _networkConfig.StaticPeers = enodesString;
        List<Node> nodes = _loader.DiscoverNodes(default).ToBlockingEnumerable().ToList();
        Assert.That(nodes.Count, Is.EqualTo(2));
        foreach (Node node in nodes)
        {
            Assert.That(node.IsStatic, Is.True);
        }
    }

    [Test]
    public void Can_load_bootnodes()
    {
        _discoveryConfig.Bootnodes = enodesString;
        _networkConfig.Bootnodes = _discoveryConfig.Bootnodes;
        List<Node> nodes = _loader.DiscoverNodes(default).ToBlockingEnumerable().ToList();
        Assert.That(nodes.Count, Is.EqualTo(2));
        foreach (Node node in nodes)
        {
            Assert.That(node.IsBootnode, Is.True);
        }
    }

    [Test]
    public void Can_load_persisted()
    {
        _peerStorage.GetPersistedNodes().Returns(new[] { new NetworkNode(enode1String), new NetworkNode(enode2String) });
        List<Node> nodes = _loader.DiscoverNodes(default).ToBlockingEnumerable().ToList();
        Assert.That(nodes.Count, Is.EqualTo(2));
        foreach (Node node in nodes)
        {
            Assert.That(node.IsBootnode, Is.False);
            Assert.That(node.IsStatic, Is.False);
        }
    }

    [Test]
    public void Can_load_only_static_nodes()
    {
        _networkConfig.StaticPeers = enode1String;
        _networkConfig.Bootnodes = enode2String;
        _networkConfig.OnlyStaticPeers = true;
        List<Node> nodes = _loader.DiscoverNodes(default).ToBlockingEnumerable().ToList();
        Assert.That(nodes.Count, Is.EqualTo(1));
        foreach (Node node in nodes)
        {
            Assert.That(node.IsStatic, Is.True);
        }
    }
}
