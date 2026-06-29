// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
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
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Network.Enr;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Network.Discovery.Test;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class DiscoveryV5AppTests
{
    private MemDb _discoveryDb = null!;
    private DiscoveryV5App _discoveryV5App = null!;
    private readonly List<IContainer> _containers = [];

    [SetUp]
    public void Setup()
    {
        _discoveryDb = new MemDb();
        _discoveryV5App = CreateDiscoveryV5App(IPAddress.Parse("8.8.8.8"));
    }

    private DiscoveryV5App CreateDiscoveryV5App(IPAddress externalIp, Action<ContainerBuilder>? configureDiscv5Services = null)
    {
        NetworkConfig networkConfig = new()
        {
            Bootnodes = [],
            ExternalIp = externalIp.ToString()
        };
        IProtectedPrivateKey nodeKey = new InsecureProtectedPrivateKey(TestItem.PrivateKeyF);
        IEnode enode = new Enode(nodeKey.PublicKey, externalIp, networkConfig.P2PPort, networkConfig.DiscoveryPort);
        IIPResolver ipResolver = new FixedIpResolver(networkConfig);
        EthereumEcdsa ecdsa = new(0);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        Block head = Build.A.Block.Genesis.TestObject;
        blockTree.Head.Returns(head);
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        forkInfo.GetForkId(head.Header.Number, head.Header.Timestamp).Returns(new Nethermind.Network.ForkId(0, 0));
        ContainerBuilder builder = new();
        builder.RegisterInstance(LimboLogs.Instance).As<ILogManager>();
        builder.RegisterInstance(networkConfig).As<INetworkConfig>();
        builder.RegisterInstance(enode).As<IEnode>();
        builder.RegisterInstance(ipResolver).As<IIPResolver>();
        builder.RegisterInstance(nodeKey).Keyed<IProtectedPrivateKey>(IProtectedPrivateKey.NodeKey);
        builder.RegisterInstance(ecdsa).As<IEthereumEcdsa>().As<IEcdsa>();
        builder.RegisterInstance(blockTree).As<IBlockTree>();
        builder.RegisterInstance(forkInfo).As<IForkInfo>();
        builder.RegisterInstance(Timestamper.Default).As<ITimestamper>();
        builder.RegisterInstance(new CryptoRandom()).As<ICryptoRandom>();
        builder.RegisterInstance(new NetworkStorage(_discoveryDb, LimboLogs.Instance)).Keyed<INetworkStorage>(DbNames.DiscoveryV5Nodes);
        builder.RegisterInstance(Substitute.For<INodeStatsManager>()).As<INodeStatsManager>();
        builder.RegisterType<NodeRecordProvider>().As<INodeRecordProvider>().WithAttributeFiltering().SingleInstance();
        IContainer container = builder.Build();
        _containers.Add(container);

        return new DiscoveryV5App(
            container,
            nodeKey,
            enode,
            ipResolver,
            networkConfig,
            new DiscoveryConfig { },
            new ProcessExitSource(CancellationToken.None),
            LimboLogs.Instance,
            configureDiscv5Services
        );
    }

    [TearDown]
    public async Task Teardown()
    {
        if (_discoveryV5App is not null)
        {
            await _discoveryV5App.DisposeAsync();
        }
        for (int i = 0; i < _containers.Count; i++)
        {
            _containers[i].Dispose();
        }
        _containers.Clear();
        _discoveryDb.Dispose();
    }

    private static NodeRecord CreateTestEnr(Nethermind.Crypto.PrivateKey privateKey, IPAddress? ipAddress = null, int port = 30303, int? udpPort = null, bool includeTcp = true, bool includeUdp = true, bool includeEth2 = false) =>
        TestEnrBuilder.BuildSigned(
            privateKey,
            ipAddress ?? IPAddress.Loopback,
            tcpPort: includeTcp ? port : null,
            udpPort: includeUdp ? udpPort ?? port : null,
            configureExtras: includeEth2 ? static enr => enr.SetEntry(new TestEth2Entry()) : null);

    private static NodeRecord CreateTestIpv6Enr(Nethermind.Crypto.PrivateKey privateKey, IPAddress ipAddress, int udpPort, bool useUdp6 = true) =>
        TestEnrBuilder.BuildSigned(privateKey, ipAddress, tcpPort: null, udpPort: udpPort, useUdp6: useUdp6);

    private static NodeRecord CreateEnrForAddress(Nethermind.Crypto.PrivateKey privateKey, IPAddress ipAddress) =>
        ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? CreateTestIpv6Enr(privateKey, ipAddress, 30303)
            : CreateTestEnr(privateKey, ipAddress);

    [Test]
    public void Should_Reject_Private_Ip_Enr()
    {
        NodeRecord enr = CreateTestEnr(TestItem.PrivateKeyA, IPAddress.Loopback);

        bool result = _discoveryV5App.TryGetAcceptableNodeFromEnr(enr, out Node? node);

        Assert.That(result, Is.False);
        Assert.That(node, Is.Null);
    }

    [Test]
    public async Task Should_Accept_Private_Ip_Enr_On_Private_Deployment()
    {
        await using DiscoveryV5App privateDiscoveryApp = CreateDiscoveryV5App(IPAddress.Loopback);
        NodeRecord enr = CreateTestEnr(TestItem.PrivateKeyA, IPAddress.Loopback);

        bool result = privateDiscoveryApp.TryGetAcceptableNodeFromEnr(enr, out Node? node);

        Assert.That(result, Is.True);
        Assert.That(node, Is.Not.Null);
        Assert.That(node!.Host, Is.EqualTo(IPAddress.Loopback.ToString()));
    }

    [TestCase("0.1.2.3")]
    [TestCase("192.0.0.1")]
    [TestCase("192.0.2.1")]
    [TestCase("192.31.196.1")]
    [TestCase("192.52.193.1")]
    [TestCase("198.18.0.1")]
    [TestCase("192.175.48.1")]
    [TestCase("198.51.100.1")]
    [TestCase("203.0.113.1")]
    [TestCase("240.0.0.1")]
    [TestCase("::ffff:224.0.0.1")]
    [TestCase("64:ff9b::1")]
    [TestCase("100::1")]
    [TestCase("2001:db8::1")]
    [TestCase("2002::1")]
    [TestCase("3fff::1")]
    public void Should_Reject_Special_Use_Ip_Enr(string ip)
    {
        NodeRecord enr = CreateEnrForAddress(TestItem.PrivateKeyA, IPAddress.Parse(ip));

        bool result = _discoveryV5App.TryGetAcceptableNodeFromEnr(enr, out Node? node);

        Assert.That(result, Is.False);
        Assert.That(node, Is.Null);
    }

    [TestCase("192.0.2.1")]
    [TestCase("2001:db8::1")]
    public async Task Should_Reject_Special_Use_Ip_Enr_On_Private_Deployment(string ip)
    {
        await using DiscoveryV5App privateDiscoveryApp = CreateDiscoveryV5App(IPAddress.Loopback);
        NodeRecord enr = CreateEnrForAddress(TestItem.PrivateKeyA, IPAddress.Parse(ip));

        bool result = privateDiscoveryApp.TryGetAcceptableNodeFromEnr(enr, out Node? node);

        Assert.That(result, Is.False);
        Assert.That(node, Is.Null);
    }

    [Test]
    public void Should_Accept_Public_Ip_Enr()
    {
        NodeRecord enr = CreateTestEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"));

        bool result = _discoveryV5App.TryGetAcceptableNodeFromEnr(enr, out Node? node);

        Assert.That(result, Is.True);
        Assert.That(node, Is.Not.Null);
        Assert.That(node!.Host, Is.EqualTo("8.8.8.8"));
    }

    [Test]
    public void Should_Reject_Consensus_Only_Enr()
    {
        NodeRecord enr = CreateTestEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), includeEth2: true);
        NodeRecord decoded = NodeRecord.FromEnrString(enr.ToString());

        bool result = _discoveryV5App.TryGetAcceptableNodeFromEnr(decoded, out Node? node);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.HasEntry(EnrContentKey.Eth2), Is.True);
            Assert.That(result, Is.False);
            Assert.That(node, Is.Null);
        }
    }

    [Test]
    public async Task AddNodeToDiscovery_ShouldSkipNodeWithoutEnr()
    {
        IKademlia<PublicKey, Node> kademlia = Substitute.For<IKademlia<PublicKey, Node>>();
        DiscoveryV5App discoveryV5App = CreateDiscoveryV5App(
            IPAddress.Parse("8.8.8.8"),
            builder => builder.RegisterInstance(kademlia).As<IKademlia<PublicKey, Node>>());

        try
        {
            discoveryV5App.AddNodeToDiscovery(new Node(TestItem.PublicKeyA, "8.8.8.8", 30303));

            kademlia.DidNotReceive().AddOrRefresh(Arg.Any<Node>());
        }
        finally
        {
            await discoveryV5App.DisposeAsync();
        }
    }

    [Test]
    public async Task AddNodeToDiscovery_ShouldAddValidatedEnrNode()
    {
        IKademlia<PublicKey, Node> kademlia = Substitute.For<IKademlia<PublicKey, Node>>();
        DiscoveryV5App discoveryV5App = CreateDiscoveryV5App(
            IPAddress.Parse("8.8.8.8"),
            builder => builder.RegisterInstance(kademlia).As<IKademlia<PublicKey, Node>>());
        NodeRecord enr = CreateTestEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), udpPort: 30304);
        Node node = new(TestItem.PrivateKeyA.PublicKey, "1.1.1.1", 30303)
        {
            Enr = enr
        };

        try
        {
            discoveryV5App.AddNodeToDiscovery(node);

            kademlia.Received(1).AddOrRefresh(Arg.Is<Node>(added =>
                added.Id.Equals(TestItem.PrivateKeyA.PublicKey) &&
                added.Host == "8.8.8.8" &&
                added.Port == 30304 &&
                added.Enr == enr));
        }
        finally
        {
            await discoveryV5App.DisposeAsync();
        }
    }

    [Test]
    public async Task AddNodeToDiscovery_ShouldSkipMismatchedEnr()
    {
        IKademlia<PublicKey, Node> kademlia = Substitute.For<IKademlia<PublicKey, Node>>();
        DiscoveryV5App discoveryV5App = CreateDiscoveryV5App(
            IPAddress.Parse("8.8.8.8"),
            builder => builder.RegisterInstance(kademlia).As<IKademlia<PublicKey, Node>>());
        NodeRecord enr = CreateTestEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"));
        Node node = new(TestItem.PrivateKeyB.PublicKey, "8.8.8.8", 30303)
        {
            Enr = enr
        };

        try
        {
            discoveryV5App.AddNodeToDiscovery(node);

            kademlia.DidNotReceive().AddOrRefresh(Arg.Any<Node>());
        }
        finally
        {
            await discoveryV5App.DisposeAsync();
        }
    }

    [Test]
    public void Should_Use_Udp_Port_From_Enr()
    {
        NodeRecord enr = CreateTestEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), port: 30303, udpPort: 30304);

        bool result = _discoveryV5App.TryGetAcceptableNodeFromEnr(enr, out Node? node);

        Assert.That(result, Is.True);
        Assert.That(node, Is.Not.Null);
        Assert.That(node!.Port, Is.EqualTo(30304));
    }

    [Test]
    public void Should_Reject_Tcp_Only_Enr()
    {
        NodeRecord enr = CreateTestEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), includeTcp: true, includeUdp: false);

        bool result = _discoveryV5App.TryGetAcceptableNodeFromEnr(enr, out Node? node);

        Assert.That(result, Is.False);
        Assert.That(node, Is.Null);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Should_Accept_Ipv6_Enr(bool useUdp6)
    {
        NodeRecord enr = CreateTestIpv6Enr(TestItem.PrivateKeyA, IPAddress.Parse("2001:4860:4860::8888"), 9001, useUdp6);

        bool result = _discoveryV5App.TryGetAcceptableNodeFromEnr(enr, out Node? node);

        Assert.That(result, Is.True);
        Assert.That(node, Is.Not.Null);
        Assert.That(node!.Host, Is.EqualTo("2001:4860:4860::8888"));
        Assert.That(node.Port, Is.EqualTo(9001));
    }

    [Test]
    public void Should_Use_Udp_Port_From_Configured_Enr_Bootnode()
    {
        NodeRecord enr = CreateTestEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), udpPort: 9001, includeTcp: false);
        NetworkConfig networkConfig = new()
        {
            Bootnodes = [new NetworkNode(enr.ToString())]
        };
        DiscoveryConfig discoveryConfig = new()
        {
            UseDefaultDiscv5Bootnodes = false
        };

        List<Node> bootNodes = _discoveryV5App.CreateBootNodes(networkConfig, discoveryConfig);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bootNodes, Has.Count.EqualTo(1));
            Assert.That(bootNodes[0].Port, Is.EqualTo(9001));
            Assert.That(bootNodes[0].Enr?.ToString(), Is.EqualTo(enr.ToString()));
        }
    }

    [Test]
    public void Should_Use_Discovery_Port_From_Configured_Enode_Bootnode()
    {
        Enode enode = new(TestItem.PrivateKeyA.PublicKey, IPAddress.Parse("8.8.8.8"), 30303, discoveryPort: 9001);
        NetworkConfig networkConfig = new()
        {
            Bootnodes = [new NetworkNode(enode)]
        };
        DiscoveryConfig discoveryConfig = new()
        {
            UseDefaultDiscv5Bootnodes = false
        };

        List<Node> bootNodes = _discoveryV5App.CreateBootNodes(networkConfig, discoveryConfig);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bootNodes, Has.Count.EqualTo(1));
            Assert.That(bootNodes[0].Port, Is.EqualTo(9001));
            Assert.That(bootNodes[0].Host, Is.EqualTo("8.8.8.8"));
        }
    }
}
