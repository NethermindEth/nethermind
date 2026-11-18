// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Init.Modules;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Enr;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;
using System.Linq;
using Nethermind.Core.Test.Threading;
using Nethermind.Db;
using Nethermind.Network.Discovery.Discv4.Kademlia;
using Nethermind.Network.Discovery.Kademlia;

namespace Nethermind.Network.Discovery.Test.Discv4;

public class DiscoveryAppTests
{
    [Test]
    public async Task Production_discv4_scope_shares_online_activity_and_the_lookup_budget()
    {
        using MemDb db = new();
        NetworkStorage storage = new(db, LimboLogs.Instance);
        using PeerAddressPolicy policy = new(new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Parse("8.8.8.8")),
            new LocalAddressSource(LimboLogs.Instance, () => []));
        await using IContainer container = CreateStartupContainer(storage, policy, CancellationToken.None);
        ILifetimeScope? scope = null;
        await using DiscoveryApp app = container.Resolve<DiscoveryApp>(TypedParameter.From<Action<ContainerBuilder>>(builder =>
            builder.RegisterBuildCallback(child => scope = child)));
        ILifetimeScope current = scope!;
        DiscoveryPersistenceManager persistence = current.Resolve<DiscoveryPersistenceManager>();
        KademliaAdapter adapter = (KademliaAdapter)current.Resolve<IKademliaAdapter>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(current.Resolve<KademliaAdapter>(), Is.SameAs(adapter));
            Assert.That(adapter.RequestActivity, Is.SameAs(persistence.RequestActivity));
            Assert.That(current.Resolve<KademliaConfig<Node>>().LookupFindNeighbourHardTimeout, Is.EqualTo(TimeSpan.FromMilliseconds(6000)));
            Assert.That(current.Resolve<IRoutingTable<Node, ValueHash256>>(), Is.SameAs(current.Resolve<IHealthRoutingTable<Node, ValueHash256>>()));
        }
    }

    [Test]
    public void Should_use_discovery_port_from_configured_enode_bootnode()
    {
        Enode enode = new(TestItem.PrivateKeyA.PublicKey, IPAddress.Parse("8.8.8.8"), 30303, discoveryPort: 9001);

        List<Node> bootNodes = DiscoveryApp.CreateBootNodes(
            [new NetworkNode(enode)],
            LimboLogs.Instance.GetClassLogger<DiscoveryAppTests>(),
            IPAddress.Any);

        Assert.That(bootNodes, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bootNodes[0].Port, Is.EqualTo(30303));
            Assert.That(bootNodes[0].DiscoveryPort, Is.EqualTo(9001));
            Assert.That(bootNodes[0].Host, Is.EqualTo("8.8.8.8"));
        }
    }

    [TestCase("0.0.0.0", "8.8.8.8", 1)]
    [TestCase("0.0.0.0", "2001:4860:4860::8888", 0)]
    [TestCase("2001:4860:4860::8844", "8.8.8.8", 0)]
    [TestCase("::", "8.8.8.8", 1)]
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
        Enode enode = new(TestItem.PrivateKeyA.PublicKey, IPAddress.Parse("::ffff:8.8.8.1"), 30303);

        List<Node> bootNodes = DiscoveryApp.CreateBootNodes(
            [new NetworkNode(enode)],
            LimboLogs.Instance.GetClassLogger<DiscoveryAppTests>(),
            IPAddress.Any);

        Assert.That(bootNodes, Has.Count.EqualTo(1));
        Assert.That(bootNodes[0].DiscoveryAddress.Address, Is.EqualTo(IPAddress.Parse("8.8.8.1")));
    }

    [Test]
    public void Should_reconstruct_signed_enr_before_rejecting_generic_endpoint()
    {
        NodeRecord record = CreateAsymmetricDualStackRecord();
#pragma warning disable NUnit2045 // Keep prerequisite acquisition outside a grouped scope; its output is used by later assertions or subsequent setup/disposal.
        Assert.That(Node.TryFromEnr(record, out Node? genericNode), Is.True);
#pragma warning restore NUnit2045
        Assert.That(genericNode!.HasDiscoveryEndpoint, Is.False);
        genericNode.IsBootnode = true;
        genericNode.SetVerifiedEnr(record);
        genericNode.ObserveEnrSequence(record.EnrSequence + 1);

        bool result = DiscoveryApp.TryCreateReachableNode(
            genericNode,
            IPAddress.Parse("2001:4860::5"),
            out Node? reachableNode, allowNonRoutable: PeerAddressPermissions.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True);
            Assert.That(reachableNode, Is.Not.Null);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reachableNode!.Host, Is.EqualTo("2001:4860::1"));
            Assert.That(reachableNode.Port, Is.EqualTo(30303));
            Assert.That(reachableNode.DiscoveryPort, Is.EqualTo(30304));
            Assert.That(reachableNode.IsBootnode, Is.True);
            Assert.That(reachableNode.IsVerifiedEnr(record), Is.True);
            Assert.That(reachableNode.HighestObservedEnrSequence, Is.EqualTo(record.EnrSequence + 1));
        }
    }

    [Test]
    public void Should_keep_reachable_caller_node_when_signed_enr_matches_identity()
    {
        IPAddress address = IPAddress.Parse("8.8.8.1");
        NodeRecord record = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyA, address, tcpPort: 30303, udpPort: 30304);
        Node callerNode = new(TestItem.PublicKeyA, address.ToString(), 30303, 30304)
        {
            ClientId = "Nethermind/v1.0.0",
            EthDetails = "eth/68",
            Enr = record,
            IsBootnode = true,
            IsStatic = true,
        };
        bool result = DiscoveryApp.TryCreateReachableNode(callerNode, IPAddress.Any, out Node? reachableNode, allowNonRoutable: PeerAddressPermissions.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True);
            Assert.That(reachableNode, Is.SameAs(callerNode));
        }
    }

    [Test]
    public void Should_reject_reachable_caller_node_when_signed_enr_has_different_identity()
    {
        IPAddress address = IPAddress.Parse("8.8.8.1");
        Node callerNode = new(TestItem.PublicKeyA, address.ToString(), 30303, 30304)
        {
            Enr = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyB, address, tcpPort: 30303, udpPort: 30304),
        };

        bool result = DiscoveryApp.TryCreateReachableNode(callerNode, IPAddress.Any, out Node? reachableNode, allowNonRoutable: PeerAddressPermissions.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.False);
            Assert.That(reachableNode, Is.Null);
        }
    }

    [Test]
    public void Should_use_configured_enr_bootnode()
    {
        NodeRecord enr = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), tcpPort: null, udpPort: 9001);

        List<Node> bootNodes = DiscoveryApp.CreateBootNodes(
            [new NetworkNode(enr.ToString())],
            LimboLogs.Instance.GetClassLogger<DiscoveryAppTests>(),
            IPAddress.Any);

        Assert.That(bootNodes, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
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
            IPAddress.Parse("8.8.8.1"),
            tcpPort: 30303,
            udpPort: 30304,
            configureExtras: record =>
            {
                record.SetEntry(new Ip6Entry(IPAddress.Parse("2001:4860::1")));
                record.SetEntry(new Tcp6Entry(30305));
                record.SetEntry(new Udp6Entry(30306));
            });

        List<Node> bootNodes = DiscoveryApp.CreateBootNodes(
            [new NetworkNode(enr.ToString())],
            LimboLogs.Instance.GetClassLogger<DiscoveryAppTests>(),
            IPAddress.Parse("2001:4860::5"));

        Assert.That(bootNodes, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bootNodes[0].Host, Is.EqualTo("2001:4860::1"));
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
            IPAddress.Parse("2001:4860::5"), allowNonRoutable: PeerAddressPermissions.None);

        Assert.That(restoredNode, Is.Not.Null);
        NodeRecord restoredEnr = restoredNode!.Enr ?? throw new AssertionException("Expected the restored node to retain its ENR.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(restoredNode.Host, Is.EqualTo("2001:4860::1"));
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

        InitConfig initConfig = new() { DiscoveryEnabled = true, DiagnosticMode = DiagnosticMode.MemDb };
        ContainerBuilder builder = new();
        builder.RegisterModule(new DiscoveryModule(initConfig, networkConfig));
        builder.RegisterInstance(initConfig).As<IInitConfig>();
        builder.RegisterInstance(new ChainSpec());
        builder.RegisterInstance(LimboLogs.Instance).As<ILogManager>();
        builder.RegisterInstance(networkConfig).As<INetworkConfig>();
        builder.RegisterInstance(discoveryConfig).As<IDiscoveryConfig>();
        builder.RegisterInstance(ipResolver).As<IIPResolver>();
        builder.RegisterInstance(new PeerAddressPolicy(ipResolver, new LocalAddressSource(LimboLogs.Instance, () => [])));
        builder.RegisterInstance(listenerState);
        builder.RegisterInstance(processExitSource).As<IProcessExitSource>();
        builder.RegisterInstance<IEcdsa>(new EthereumEcdsa(0));
        builder.RegisterInstance(Timestamper.Default).As<ITimestamper>();
        builder.RegisterInstance(Substitute.For<IForkInfo>());
        builder.RegisterInstance(Substitute.For<INodeRecordProvider>());
        builder.RegisterInstance(Substitute.For<INodeStatsManager>());
        builder.RegisterInstance(new Enode(TestItem.PrivateKeyF.PublicKey, IPAddress.Parse("8.8.8.8"), 30303, 30303)).As<IEnode>();
        await using IContainer container = builder.Build();
        await using DiscoveryApp app = container.Resolve<DiscoveryApp>(TypedParameter.From<Action<ContainerBuilder>>(
            services => services.RegisterInstance(kademlia).As<IKademlia<PublicKey, Node>>()));
        listenerState.SetDiscoveryAddress(IPAddress.Any);

        await app.StartAsync();

        kademlia.Received(1).Remove(Arg.Is<Node>(node =>
            node.DiscoveryAddress.Address.Equals(IPAddress.Parse("2001:4860:4860::8888"))));
        kademlia.DidNotReceive().AddOrRefresh(Arg.Any<Node>());
    }

    [Test]
    public void Persisted_unroutable_nodes_are_rejected(
        [Values("0.0.0.0", "127.0.0.1", "10.0.0.1", "224.0.0.1", "::", "::1", "fe80::1", "fc00::1", "::ffff:127.0.0.1")] string address,
        [Values] bool enr)
    {
        NetworkNode stored = enr
            ? new NetworkNode(TestEnrBuilder.BuildSigned(TestItem.PrivateKeyA, IPAddress.Parse(address), tcpPort: 30303, udpPort: 30304).ToString())
            : new NetworkNode(new Enode(TestItem.PublicKeyA, IPAddress.Parse(address), 30303));

        IIPResolver resolver = Substitute.For<IIPResolver>();
        resolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
            new IIPResolver.NethermindIp(IPAddress.IPv6Any, IPAddress.None)));
        PeerAddressPolicy policy = new(resolver, new LocalAddressSource(LimboLogs.Instance,
            () => [IPAddress.Loopback, IPAddress.Parse("169.254.1.2"), IPAddress.Parse("fe80::1"), IPAddress.Parse("8.8.8.8")]));
        Assert.That(DiscoveryApp.RestorePersistedNode(stored, IPAddress.IPv6Any, policy.AllowedAddresses), Is.Null);
    }

    [Test]
    public void Private_deployments_and_configured_nodes_remain_reachable([Values] bool configured)
    {
        Node node = new(TestItem.PublicKeyA, "10.0.0.2", 30303) { IsStatic = configured };
        IPAddress local = IPAddress.Parse(configured ? "8.8.8.8" : "10.0.0.1");
        Assert.That(DiscoveryApp.TryCreateReachableNode(node, local, out _, allowNonRoutable: !configured ? PeerAddressPermissions.All : PeerAddressPermissions.None), Is.True);
    }

    [Test]
    public void Wildcard_binding_uses_private_deployment_context([Values] bool allowPrivate)
    {
        NetworkNode stored = new(new Enode(TestItem.PublicKeyA, IPAddress.Parse("10.0.0.2"), 30303));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DiscoveryApp.RestorePersistedNode(stored, IPAddress.IPv6Any, allowPrivate ? PeerAddressPermissions.All : PeerAddressPermissions.None) is not null, Is.EqualTo(allowPrivate));
            Assert.That(DiscoveryApp.TryCreateReachableNode(new Node(stored), IPAddress.IPv6Any, out _, allowPrivate ? PeerAddressPermissions.All : PeerAddressPermissions.None), Is.EqualTo(allowPrivate));
        }
    }

    private static NodeRecord CreateAsymmetricDualStackRecord() =>
        TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyA,
            IPAddress.Parse("8.8.8.1"),
            tcpPort: 30303,
            udpPort: null,
            configureExtras: record =>
            {
                record.SetEntry(new Ip6Entry(IPAddress.Parse("2001:4860::1")));
                record.SetEntry(new Udp6Entry(30304));
            });

    [Test]
    [CancelAfter(10000)]
    public async Task Discv4_bootstrap_runs_during_load_and_shutdown_drains_probes(CancellationToken token)
    {
        using MemDb db = new();
        NetworkStorage storage = new(db, LimboLogs.Instance);
        storage.UpdateNodes(new[] { TestItem.PublicKeyA, TestItem.PublicKeyB, TestItem.PublicKeyC }
            .Select((key, index) => new NetworkNode(key, $"8.8.4.{index + 1}", 30303, 0)));
        storage.Commit();
        IKademlia<PublicKey, Node> table = Substitute.For<IKademlia<PublicKey, Node>>();
        table.GetAllAtDistance(0).Returns([new Node(TestItem.PublicKeyF, "8.8.8.8", 30303)]);
        TaskCompletionSource bootstrap = new(TaskCreationOptions.RunContinuationsAsynchronously);
        table.Run(Arg.Any<CancellationToken>()).Returns(call =>
        {
            bootstrap.TrySetResult();
            return Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
        });
        TaskCompletionSource probes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0;
        IKademliaAdapter adapter = Substitute.For<IKademliaAdapter>();
        adapter.Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (Interlocked.Increment(ref running) == 3) probes.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return false;
            }
            finally { Interlocked.Decrement(ref running); }
        });
        PeerAddressPolicy policy = new(new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Parse("8.8.8.8")),
            new LocalAddressSource(LimboLogs.Instance, () => []));
        await using IContainer container = CreateStartupContainer(storage, policy, token);
        await using DiscoveryApp app = container.Resolve<DiscoveryApp>(TypedParameter.From<Action<ContainerBuilder>>(services =>
        {
            services.RegisterInstance(table).As<IKademlia<PublicKey, Node>>();
            services.RegisterInstance(adapter).As<IKademliaAdapter>();
        }));
        app.InitializeChannel(Substitute.For<IDatagramSocket>(), static packet => packet.Dispose());

        await app.StartAsync();
        await Task.WhenAll(bootstrap.Task, probes.Task).WaitAsync(token);
        Assert.That(Volatile.Read(ref running), Is.EqualTo(3));
        await app.StopAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Volatile.Read(ref running), Is.Zero);
            Assert.That(storage.GetPersistedNodes().Select(node => node.NodeId),
                Is.EquivalentTo(new[] { TestItem.PublicKeyA, TestItem.PublicKeyB, TestItem.PublicKeyC }));
        }
    }

    [Test]
    [CancelAfter(20000)]
    public async Task Failed_app_startup_load_preserves_rows_and_an_explicit_complete_reload_enables_pruning(
        [Values] bool fullStore, CancellationToken token)
    {
        using MemDb db = new();
        DiscoveryPersistenceManagerTests.ObservedStorage storage = new(new NetworkStorage(db, LimboLogs.Instance));
        NetworkNode[] stored = fullStore ? DiscoveryPersistenceManagerTests.CreateRows(100) : DiscoveryPersistenceManagerTests.CreateRows(100).Take(2).ToArray();
        storage.UpdateNodes(stored);
        storage.Commit();
        storage.FailReads = true;
        PeerAddressPolicy policy = new(new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Parse("8.8.8.8")),
            new LocalAddressSource(LimboLogs.Instance, () => Array.Empty<IPAddress>()));
        ManualTimeProvider clock = new();
        IKademlia<PublicKey, Node> table = Substitute.For<IKademlia<PublicKey, Node>>();
        table.GetAllAtDistance(0).Returns([new Node(TestItem.PublicKeyF, "8.8.8.8", 30303)]);
        table.Run(Arg.Any<CancellationToken>()).Returns(call => Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()));
        IKademliaAdapter adapter = Substitute.For<IKademliaAdapter>();
        adapter.Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(false);
        DiscoveryPersistenceManager manager = null!;
        await using IContainer container = CreateStartupContainer(storage, policy, token);
        await using DiscoveryApp app = container.Resolve<DiscoveryApp>(TypedParameter.From<Action<ContainerBuilder>>(services =>
        {
            services.RegisterInstance(table).As<IKademlia<PublicKey, Node>>();
            services.RegisterInstance(adapter).As<IKademliaAdapter>();
            services.Register(context => new DiscoveryPersistenceManager(storage, context.Resolve<INodeStatsManager>(),
                adapter, table, context.Resolve<IDiscoveryConfig>(), LimboLogs.Instance, policy)
            { PersistenceTimeProvider = clock }).SingleInstance();
            services.RegisterBuildCallback(scope => manager = scope.Resolve<DiscoveryPersistenceManager>());
        }));
        app.InitializeChannel(Substitute.For<IDatagramSocket>(), static packet => packet.Dispose());
        await app.StartAsync();
        try
        {
            await clock.TimerCreated.WaitAsync(token);
            Assert.That(clock.DueTimes, Is.EqualTo(new[] { TimeSpan.FromSeconds(180) }));
            await adapter.DidNotReceive().Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>());
            storage.FailReads = false;
            Node update = new(stored[0].NodeId, stored[0].Host, 30304);
            Node newcomer = new(TestItem.PublicKeyA, "9.9.9.9", 30303);
            DiscoveryLiveness.Mark(update);
            DiscoveryLiveness.Mark(newcomer);
            table.IterateNodes().Returns([update, newcomer]);
            storage.DrainCommits();
            clock.AdvanceAndFireTimer(TimeSpan.FromSeconds(180));
            await storage.NextCommit(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(storage.GetPersistedNodes().Select(node => node.NodeId),
                    Is.EquivalentTo(fullStore ? stored.Select(node => node.NodeId) : stored.Select(node => node.NodeId).Append(newcomer.Id)));
                Assert.That(storage.PersistedNodesCount, Is.EqualTo(fullStore ? DiscoveryPersistenceManager.MaximumPersistedNodes : 3));
                Assert.That(storage.GetPersistedNodes().Single(node => node.NodeId.Equals(update.Id)).DiscoveryPort, Is.EqualTo(30304));
            }
            await manager.LoadPersistedNodes(token,
                (row, permissions) => DiscoveryApp.RestorePersistedNode(row, IPAddress.Any, permissions));
            manager.RequestActivity.Answered(manager.RequestActivity.BeginRequest());
            storage.DrainCommits();
            clock.AdvanceAndFireTimer(TimeSpan.FromSeconds(180));
            await storage.NextCommit(token);
            Assert.That(storage.GetPersistedNodes().Select(node => node.NodeId), Is.EquivalentTo(new[] { update.Id, newcomer.Id }));
        }
        finally { await app.StopAsync(); }
    }

    private static IContainer CreateStartupContainer(INetworkStorage storage, PeerAddressPolicy policy, CancellationToken token)
    {
        NetworkConfig network = new() { ExternalIp = "8.8.8.8", LocalIp = "0.0.0.0", Bootnodes = [] };
        InitConfig init = new() { DiscoveryEnabled = true, DiagnosticMode = DiagnosticMode.MemDb };
        ContainerBuilder builder = new();
        builder.RegisterModule(new DiscoveryModule(init, network));
        builder.RegisterInstance(init).As<IInitConfig>();
        builder.RegisterInstance(new ChainSpec());
        builder.RegisterInstance(LimboLogs.Instance).As<ILogManager>();
        builder.RegisterInstance(network).As<INetworkConfig>();
        builder.RegisterInstance(new DiscoveryConfig { Concurrency = 3 }).As<IDiscoveryConfig>();
        FixedIpResolver resolver = new(network);
        builder.RegisterInstance(resolver).As<IIPResolver>();
        builder.RegisterInstance(policy);
        builder.RegisterInstance(new NetworkListenerState(network, resolver, LimboLogs.Instance));
        builder.RegisterInstance(new ProcessExitSource(token)).As<IProcessExitSource>();
        builder.RegisterInstance(new Ecdsa()).As<IEcdsa>();
        builder.RegisterInstance(Timestamper.Default).As<ITimestamper>();
        builder.RegisterInstance(Substitute.For<IForkInfo>());
        builder.RegisterInstance(Substitute.For<INodeRecordProvider>());
        builder.RegisterInstance(Substitute.For<INodeStatsManager>());
        builder.RegisterInstance(Substitute.For<IMessageSerializationService>());
        builder.RegisterInstance(storage).Keyed<INetworkStorage>(DbNames.DiscoveryNodes);
        builder.RegisterInstance(new Enode(TestItem.PublicKeyF, IPAddress.Parse("8.8.8.8"), 30303)).As<IEnode>();
        return builder.Build();
    }
}
