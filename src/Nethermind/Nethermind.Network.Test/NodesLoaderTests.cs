// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
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
    private readonly List<PeerAddressPolicy> _addressPolicies = [];

    private PeerAddressPolicy OwnPolicy(PeerAddressPolicy policy)
    {
        _addressPolicies.Add(policy);
        return policy;
    }

    private void DisposePolicies()
    {
        foreach (PeerAddressPolicy policy in _addressPolicies) policy.Dispose();
        _addressPolicies.Clear();
    }

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
        _enode = new Enode(TestItem.PublicKeyA, IPAddress.Parse("8.8.8.8"), 30303);
        _loader = CreateLoader();
    }

    private NodesLoader CreateLoader(bool loadBootnodesAsPeerCandidates = true) =>
        new(_networkConfig, _statsManager, _peerStorage, _enode, LimboLogs.Instance,
            new NodesLoaderOptions(LoadBootnodesAsPeerCandidates: loadBootnodesAsPeerCandidates),
            OwnPolicy(new PeerAddressPolicy(new IIPResolver.NethermindIp(IPAddress.Any, _enode.HostIp), new LocalAddressSource(LimboLogs.Instance, () => []))));

    [TearDown]
    public void TearDown() => DisposePolicies();

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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(node.IsBootnode, Is.False);
                Assert.That(node.IsStatic, Is.False);
            }
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

    [TestCase("0.0.0.0", 30303, false)]
    [TestCase("127.0.0.1", 30303, false)]
    [TestCase("10.1.2.3", 30303, false)]
    [TestCase("224.0.0.1", 30303, false)]
    [TestCase("255.255.255.255", 30303, false)]
    [TestCase("::", 30303, false)]
    [TestCase("::1", 30303, false)]
    [TestCase("fc00::1", 30303, false)]
    [TestCase("::ffff:127.0.0.1", 30303, false)]
    [TestCase("8.8.8.8", 0, false)]
    [TestCase("8.8.8.8", 30303, true)]
    [TestCase("::ffff:8.8.8.8", 30303, true)]
    [TestCase("2606:4700:4700::1111", 30303, true)]
    public void Persisted_addresses_are_validated_before_stats_allocation(string host, int port, bool accepted)
    {
        _peerStorage.GetPersistedNodes().Returns([new NetworkNode(TestItem.PublicKeyB, host, port)]);

        Node[] nodes = _loader.DiscoverNodes(default).ToBlockingEnumerable().ToArray();

        Assert.That(nodes.Length, Is.EqualTo(accepted ? 1 : 0));
        _statsManager.Received(accepted ? 1 : 0).GetStatsOrNull(Arg.Any<Node>());
    }

    [Test]
    public void Explicit_static_private_peer_is_still_loaded()
    {
        NetworkNode node = new(TestItem.PublicKeyB, "10.1.2.3", 30303);
        _peerStorage.GetPersistedNodes().Returns([node]);
        _networkConfig.StaticPeers = node.ToString();

        Node[] nodes = _loader.DiscoverNodes(default).ToBlockingEnumerable().ToArray();

        Assert.That(nodes, Has.Length.EqualTo(1));
        Assert.That(nodes[0].IsStatic, Is.True);
    }

    [TestCase(4, 4)]
    [TestCase(64, 16)]
    public void Persisted_load_obeys_total_and_normalized_address_limits(int totalLimit, int expected)
    {
        _networkConfig.MaxPersistedPeerCount = totalLimit;
        NetworkNode[] stored = new NetworkNode[20];
        for (int i = 0; i < stored.Length; i++)
        {
            using PrivateKey key = new((i + 1).ToString("x64"));
            stored[i] = new NetworkNode(key.PublicKey, i % 2 == 0 ? "8.8.8.8" : "::ffff:8.8.8.8", 30303, i);
        }
        _peerStorage.GetPersistedNodes().Returns(stored);

        Node[] nodes = _loader.DiscoverNodes(default).ToBlockingEnumerable().ToArray();

        Assert.That(nodes, Has.Length.EqualTo(expected));
    }

    [Test]
    public async Task Configured_bad_node_does_not_hide_following_valid_bootnode([Values] bool invalidFirst)
    {
        NetworkNode valid = new(TestItem.PublicKeyB, "8.8.4.4", 30303, 0);
        NetworkNode invalid = new(TestItem.PublicKeyC, "8.8.4.5", -1, 0);
        NetworkConfig config = new() { Bootnodes = invalidFirst ? [invalid, valid] : [valid] };
        NodesLoader loader = new(config, Substitute.For<INodeStatsManager>(), Substitute.For<INetworkStorage>(),
            new Enode(TestItem.PublicKeyA, IPAddress.Parse("8.8.8.8"), 30303), LimboLogs.Instance, new NodesLoaderOptions(),
            OwnPolicy(new PeerAddressPolicy(new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Parse("8.8.8.8")), new LocalAddressSource(LimboLogs.Instance, () => []))));

        Node[] nodes = await loader.DiscoverNodes(default).ToArrayAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes.Select(node => node.Id), Is.EqualTo(new[] { TestItem.PublicKeyB }));
            Assert.That(nodes[0].IsBootnode, Is.True);
            Assert.That(nodes[0].Port, Is.EqualTo(30303));
        }
    }
}
