// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.Self)]
public class NodesLoaderTests
{
    private NetworkConfig _networkConfig;
    private INodeStatsManager _statsManager;
    private INetworkStorage _peerStorage;
    private IEnode _enode;
    private NodesLoader _loader;

    [SetUp]
    public void SetUp()
    {
        _networkConfig = new NetworkConfig();
        _statsManager = Substitute.For<INodeStatsManager>();
        _peerStorage = Substitute.For<INetworkStorage>();
        _enode = new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303);
        _loader = CreateLoader();
    }

    private NodesLoader CreateLoader(bool loadBootnodesAsPeerCandidates = true) =>
        new(_networkConfig, _statsManager, _peerStorage, _enode, LimboLogs.Instance,
            new NodesLoaderOptions(LoadBootnodesAsPeerCandidates: loadBootnodesAsPeerCandidates));

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
        _networkConfig.Bootnodes = new[] { new NetworkNode(enode1String), new NetworkNode(enode2String) };
        List<Node> nodes = _loader.DiscoverNodes(default).ToBlockingEnumerable().ToList();
        Assert.That(nodes.Count, Is.EqualTo(2));
        foreach (Node node in nodes)
        {
            Assert.That(node.IsBootnode, Is.True);
        }
    }

    [Test]
    public void Does_not_load_bootnodes_as_peer_candidates_when_only_discv5_is_enabled()
    {
        _networkConfig.Bootnodes = new[] { new NetworkNode(enode1String), new NetworkNode(enode2String) };
        _loader = CreateLoader(loadBootnodesAsPeerCandidates: false);

        List<Node> nodes = _loader.DiscoverNodes(default).ToBlockingEnumerable().ToList();

        Assert.That(nodes, Is.Empty);
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
        _networkConfig.Bootnodes = new[] { new NetworkNode(enode2String) };
        _networkConfig.OnlyStaticPeers = true;
        List<Node> nodes = _loader.DiscoverNodes(default).ToBlockingEnumerable().ToList();
        Assert.That(nodes.Count, Is.EqualTo(1));
        foreach (Node node in nodes)
        {
            Assert.That(node.IsStatic, Is.True);
        }
    }
}
