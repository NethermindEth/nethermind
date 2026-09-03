// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Enr;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4;

public class DiscoveryAppTests
{
    [Test]
    public void Should_use_discovery_port_from_configured_enode_bootnode()
    {
        Enode enode = new(TestItem.PrivateKeyA.PublicKey, IPAddress.Parse("8.8.8.8"), 30303, discoveryPort: 9001);

        List<Node> bootNodes = DiscoveryApp.CreateBootNodes(
            [new NetworkNode(enode)],
            LimboLogs.Instance.GetClassLogger<DiscoveryAppTests>(),
            IPAddress.Any);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bootNodes, Has.Count.EqualTo(1));
            Assert.That(bootNodes[0].Port, Is.EqualTo(30303));
            Assert.That(bootNodes[0].DiscoveryPort, Is.EqualTo(9001));
            Assert.That(bootNodes[0].Host, Is.EqualTo("8.8.8.8"));
        }
    }

    [TestCase("0.0.0.0", "8.8.8.8", 1)]
    [TestCase("0.0.0.0", "2001:4860:4860::8888", 0)]
    [TestCase("2001:4860:4860::8844", "8.8.8.8", 0)]
    public void Should_only_use_bootnode_families_reachable_from_listener(
        string localIp,
        string bootnodeIp,
        int expectedCount)
    {
        Enode enode = new(TestItem.PrivateKeyA.PublicKey, IPAddress.Parse(bootnodeIp), 30303);

        List<Node> bootNodes = DiscoveryApp.CreateBootNodes(
            [new NetworkNode(enode)],
            LimboLogs.Instance.GetClassLogger<DiscoveryAppTests>(),
            IPAddress.Parse(localIp));

        Assert.That(bootNodes, Has.Count.EqualTo(expectedCount));
    }

    [Test]
    public void Should_normalize_mapped_bootnode_to_ipv4()
    {
        Enode enode = new(TestItem.PrivateKeyA.PublicKey, IPAddress.Parse("::ffff:192.0.2.1"), 30303);

        List<Node> bootNodes = DiscoveryApp.CreateBootNodes(
            [new NetworkNode(enode)],
            LimboLogs.Instance.GetClassLogger<DiscoveryAppTests>(),
            IPAddress.Any);

        Assert.That(bootNodes, Has.Count.EqualTo(1));
        Assert.That(bootNodes[0].DiscoveryAddress.Address, Is.EqualTo(IPAddress.Parse("192.0.2.1")));
    }

    [Test]
    public void Should_reconstruct_signed_enr_before_rejecting_generic_endpoint()
    {
        NodeRecord record = CreateAsymmetricDualStackRecord();
        Assert.That(Node.TryFromEnr(record, out Node? genericNode), Is.True);
        Assert.That(genericNode!.HasDiscoveryEndpoint, Is.False);
        genericNode.SetVerifiedEnr(record);
        genericNode.ObserveEnrSequence(record.EnrSequence + 1);

        bool result = DiscoveryApp.TryCreateReachableNode(
            genericNode,
            IPAddress.Parse("2001:db8::5"),
            out Node? reachableNode);

        Assert.That(result, Is.True);
        Assert.That(reachableNode, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reachableNode!.Host, Is.EqualTo("2001:db8::1"));
            Assert.That(reachableNode.Port, Is.EqualTo(30303));
            Assert.That(reachableNode.DiscoveryPort, Is.EqualTo(30304));
            Assert.That(reachableNode.IsVerifiedEnr(record), Is.True);
            Assert.That(reachableNode.HighestObservedEnrSequence, Is.EqualTo(record.EnrSequence + 1));
        }
    }

    [Test]
    public void Should_keep_reachable_caller_node_when_signed_enr_matches_identity()
    {
        IPAddress address = IPAddress.Parse("192.0.2.1");
        NodeRecord record = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyA, address, tcpPort: 30303, udpPort: 30304);
        Node callerNode = new(TestItem.PublicKeyA, address.ToString(), 30303, 30304)
        {
            ClientId = "Nethermind/v1.0.0",
            EthDetails = "eth/68",
            Enr = record,
            IsBootnode = true,
            IsStatic = true,
        };
        bool result = DiscoveryApp.TryCreateReachableNode(callerNode, IPAddress.Any, out Node? reachableNode);

        Assert.That(result, Is.True);
        Assert.That(reachableNode, Is.SameAs(callerNode));
    }

    [Test]
    public void Should_reject_reachable_caller_node_when_signed_enr_has_different_identity()
    {
        IPAddress address = IPAddress.Parse("192.0.2.1");
        Node callerNode = new(TestItem.PublicKeyA, address.ToString(), 30303, 30304)
        {
            Enr = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyB, address, tcpPort: 30303, udpPort: 30304),
        };

        bool result = DiscoveryApp.TryCreateReachableNode(callerNode, IPAddress.Any, out Node? reachableNode);

        Assert.That(result, Is.False);
        Assert.That(reachableNode, Is.Null);
    }

    [Test]
    public void Should_use_configured_enr_bootnode()
    {
        NodeRecord enr = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), tcpPort: null, udpPort: 9001);

        List<Node> bootNodes = DiscoveryApp.CreateBootNodes(
            [new NetworkNode(enr.ToString())],
            LimboLogs.Instance.GetClassLogger<DiscoveryAppTests>(),
            IPAddress.Any);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bootNodes, Has.Count.EqualTo(1));
            Assert.That(bootNodes[0].Id, Is.EqualTo(TestItem.PrivateKeyA.PublicKey));
            Assert.That(bootNodes[0].Port, Is.Zero);
            Assert.That(bootNodes[0].DiscoveryPort, Is.EqualTo(9001));
            Assert.That(bootNodes[0].Host, Is.EqualTo("8.8.8.8"));
            Assert.That(bootNodes[0].Enr?.ToString(), Is.EqualTo(enr.ToString()));
        }
    }

    [Test]
    public void Should_select_listener_family_from_configured_enr_bootnode()
    {
        NodeRecord enr = TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyA,
            IPAddress.Parse("192.0.2.1"),
            tcpPort: 30303,
            udpPort: 30304,
            configureExtras: record =>
            {
                record.SetEntry(new Ip6Entry(IPAddress.Parse("2001:db8::1")));
                record.SetEntry(new Tcp6Entry(30305));
                record.SetEntry(new Udp6Entry(30306));
            });

        List<Node> bootNodes = DiscoveryApp.CreateBootNodes(
            [new NetworkNode(enr.ToString())],
            LimboLogs.Instance.GetClassLogger<DiscoveryAppTests>(),
            IPAddress.Parse("2001:db8::5"));

        Assert.That(bootNodes, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bootNodes[0].Host, Is.EqualTo("2001:db8::1"));
            Assert.That(bootNodes[0].Port, Is.EqualTo(30305));
            Assert.That(bootNodes[0].DiscoveryPort, Is.EqualTo(30306));
        }
    }

    [Test]
    public void Should_restore_persisted_enr_using_listener_address_family()
    {
        NodeRecord record = CreateAsymmetricDualStackRecord();
        NetworkNode persistedNode = new(record.ToString());

        Node? restoredNode = DiscoveryApp.RestorePersistedNode(
            persistedNode,
            IPAddress.Parse("2001:db8::5"));

        Assert.That(restoredNode, Is.Not.Null);
        NodeRecord restoredEnr = restoredNode!.Enr ?? throw new AssertionException("Expected the restored node to retain its ENR.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(restoredNode.Host, Is.EqualTo("2001:db8::1"));
            Assert.That(restoredNode.Port, Is.EqualTo(30303));
            Assert.That(restoredNode.DiscoveryPort, Is.EqualTo(30304));
            Assert.That(restoredEnr.GetHex(), Is.EqualTo(record.GetHex()));
        }
    }

    [Test]
    public void Should_ignore_configured_enr_without_udp_endpoint()
    {
        NodeRecord enr = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), tcpPort: 30303, udpPort: null);

        List<Node> bootNodes = DiscoveryApp.CreateBootNodes(
            [new NetworkNode(enr.ToString())],
            LimboLogs.Instance.GetClassLogger<DiscoveryAppTests>(),
            IPAddress.Any);

        Assert.That(bootNodes, Is.Empty);
    }

    [Test]
    public async Task StartAsync_ShouldRemoveBootnodeOutsideBoundFamilyAfterFallback()
    {
        NodeRecord enr = TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyA,
            IPAddress.Parse("2001:4860:4860::8888"),
            tcpPort: 9001,
            udpPort: 9001);
        NetworkConfig networkConfig = new()
        {
            Bootnodes = [new NetworkNode(enr.ToString())],
            ExternalIp = "8.8.8.8",
            LocalIp = "::"
        };
        DiscoveryConfig discoveryConfig = new();
        IIPResolver ipResolver = new FixedIpResolver(networkConfig);
        NetworkListenerState listenerState = new(IPAddress.Any, IPAddress.IPv6Any, LimboLogs.Instance);
        IProcessExitSource processExitSource = new ProcessExitSource(CancellationToken.None);
        IKademlia<PublicKey, Node> kademlia = Substitute.For<IKademlia<PublicKey, Node>>();

        using MemDb discoveryDb = new();
        ContainerBuilder builder = new();
        builder.RegisterInstance(LimboLogs.Instance).As<ILogManager>();
        builder.RegisterInstance(networkConfig).As<INetworkConfig>();
        builder.RegisterInstance(discoveryConfig).As<IDiscoveryConfig>();
        builder.RegisterInstance(ipResolver).As<IIPResolver>();
        builder.RegisterInstance(listenerState);
        builder.RegisterInstance(processExitSource).As<IProcessExitSource>();
        builder.RegisterInstance<IEcdsa>(new EthereumEcdsa(0));
        builder.RegisterInstance(Timestamper.Default).As<ITimestamper>();
        builder.RegisterInstance(Substitute.For<IForkInfo>());
        builder.RegisterInstance(Substitute.For<INodeRecordProvider>());
        builder.RegisterInstance(Substitute.For<INodeStatsManager>());
        builder.RegisterInstance(new NetworkStorage(discoveryDb, LimboLogs.Instance))
            .Keyed<INetworkStorage>(DbNames.DiscoveryNodes);
        using IContainer container = builder.Build();
        IEnode enode = new Enode(TestItem.PrivateKeyF.PublicKey, IPAddress.Parse("8.8.8.8"), 30303, 30303);
        await using DiscoveryApp app = new(
            container,
            enode,
            networkConfig,
            discoveryConfig,
            ipResolver,
            processExitSource,
            LimboLogs.Instance,
            listenerState,
            services => services.RegisterInstance(kademlia).As<IKademlia<PublicKey, Node>>());
        listenerState.SetDiscoveryAddress(IPAddress.Any);

        await app.StartAsync();

        kademlia.Received(1).Remove(Arg.Is<Node>(node =>
            node.DiscoveryAddress.Address.Equals(IPAddress.Parse("2001:4860:4860::8888"))));
        kademlia.DidNotReceive().AddOrRefresh(Arg.Any<Node>());
    }

    private static NodeRecord CreateAsymmetricDualStackRecord() =>
        TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyA,
            IPAddress.Parse("192.0.2.1"),
            tcpPort: 30303,
            udpPort: null,
            configureExtras: record =>
            {
                record.SetEntry(new Ip6Entry(IPAddress.Parse("2001:db8::1")));
                record.SetEntry(new Udp6Entry(30304));
            });
}
