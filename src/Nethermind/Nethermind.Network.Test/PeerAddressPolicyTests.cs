// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using DotNetty.Transport.Channels;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Test.Threading;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Rlpx;
using Nethermind.Network.IP;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

public class PeerAddressPolicyTests
{
    private sealed class QueuedContext : SynchronizationContext
    {
        private Action? _continuation;
        public TaskCompletionSource Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _continuation = () => callback(state);
            Posted.SetResult();
        }

        public void Run() => _continuation!();
    }

    [TestCase(OperationalStatus.Down, NetworkInterfaceType.Ethernet, false)]
    [TestCase(OperationalStatus.Dormant, NetworkInterfaceType.Ethernet, false)]
    [TestCase(OperationalStatus.Up, NetworkInterfaceType.Loopback, false)]
    [TestCase(OperationalStatus.Up, NetworkInterfaceType.Ethernet, true)]
    public void Interface_status_and_type_control_private_address_fallback(
        OperationalStatus status, NetworkInterfaceType type, bool usable)
    {
        NetworkInterface network = Substitute.For<NetworkInterface>();
        network.OperationalStatus.Returns(status);
        network.NetworkInterfaceType.Returns(type);
        if (usable)
        {
            UnicastIPAddressInformation address = Substitute.For<UnicastIPAddressInformation>();
            address.Address.Returns(IPAddress.Parse("172.18.0.1"));
            UnicastIPAddressInformationCollection unicast = Substitute.For<UnicastIPAddressInformationCollection>();
            unicast.GetEnumerator().Returns(_ => new[] { address }.AsEnumerable().GetEnumerator());
            IPInterfaceProperties properties = Substitute.For<IPInterfaceProperties>();
            properties.UnicastAddresses.Returns(unicast);
            network.GetIPProperties().Returns(properties);
        }
        else
        {
            network.GetIPProperties().Returns(_ => throw new InvalidOperationException("Unusable interface addresses must not be read."));
        }
        LocalAddressSource addresses = new(LimboLogs.Instance, () => new[] { network });
        using PeerAddressPolicy policy = new(new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.None), addresses);

        Assert.That(policy.AllowedAddresses, Is.EqualTo(usable ? PeerAddressPermissions.Private : PeerAddressPermissions.None));
        Assert.That(policy.AllowedAddresses, Is.EqualTo(usable ? PeerAddressPermissions.Private : PeerAddressPermissions.None));
        network.Received(usable ? 1 : 0).GetIPProperties();
    }

    [Test]
    public void Resolution_failure_keeps_fallback_until_a_completed_refresh()
    {
        IIPResolver resolver = Substitute.For<IIPResolver>();
        resolver.Resolve(Arg.Any<CancellationToken>()).Returns(
            ValueTask.FromException<IIPResolver.NethermindIp>(new NetworkInformationException()));
        using PeerAddressPolicy policy = new(resolver, new LocalAddressSource(LimboLogs.Instance,
            () => [IPAddress.Loopback, IPAddress.Parse("169.254.1.2"), IPAddress.Parse("fe80::1"),
                IPAddress.Parse("172.18.0.1"), IPAddress.Parse("9.9.9.9")]));

        Assert.That(policy.AllowedAddresses, Is.EqualTo(PeerAddressPermissions.Private));
        resolver.Resolved += Raise.Event<EventHandler<IIPResolver.NethermindIp>>(resolver,
            new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Parse("8.8.8.8")));
        Assert.That(policy.AllowedAddresses, Is.EqualTo(PeerAddressPermissions.None));
        resolver.Received(1).Resolve(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Production_policy_uses_registered_interfaces_when_external_address_is_unresolved()
    {
        int scans = 0;
        LocalAddressSource source = new(LimboLogs.Instance, () =>
        {
            scans++;
            return [IPAddress.Loopback, IPAddress.Parse("169.254.1.2"), IPAddress.Parse("172.18.0.1"), IPAddress.Parse("8.8.8.8")];
        });
        IIPResolver resolver = Substitute.For<IIPResolver>();
        resolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
            new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.None)));
        await using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<IIPResolver>(resolver)
            .AddSingleton(source)
            .Build();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Resolve<PeerAddressPolicy>().AllowedAddresses, Is.EqualTo(PeerAddressPermissions.Private));
            Assert.That(container.Resolve<LocalAddressSource>(), Is.SameAs(source));
            Assert.That(scans, Is.EqualTo(1));
        }
    }

    [Test]
    public void Unchanged_resolution_refresh_observes_a_new_private_interface()
    {
        IIPResolver resolver = Substitute.For<IIPResolver>();
        IIPResolver.NethermindIp addresses = new(IPAddress.Any, IPAddress.None);
        resolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(addresses));
        bool privateInterface = false;
        int scans = 0;
        using PeerAddressPolicy policy = new(resolver, new LocalAddressSource(LimboLogs.Instance, () =>
        {
            scans++;
            return [IPAddress.Parse(privateInterface ? "172.18.0.1" : "8.8.8.8")];
        }));
        Assert.That(policy.AllowedAddresses, Is.EqualTo(PeerAddressPermissions.None));
        privateInterface = true;
        resolver.Resolved += Raise.Event<EventHandler<IIPResolver.NethermindIp>>(resolver, addresses);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy.AllowedAddresses, Is.EqualTo(PeerAddressPermissions.Private));
            Assert.That(scans, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task Pending_resolution_does_not_block_policy_construction_or_reads()
    {
        TaskCompletionSource<IIPResolver.NethermindIp> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IIPResolver resolver = Substitute.For<IIPResolver>();
        resolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(completion.Task));
        QueuedContext context = new();
        PeerAddressPolicy? policy = null;
        Task<PeerAddressPermissions> read = Task.Run(() =>
        {
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                policy = new(resolver, new LocalAddressSource(LimboLogs.Instance, () => [IPAddress.Parse("172.18.0.1")]));
                return policy.AllowedAddresses;
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });
        IIPResolver.NethermindIp resolved = new(IPAddress.Any, IPAddress.Parse("8.8.8.8"));
        try
        {
            Assert.That(await read.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(PeerAddressPermissions.Private));
        }
        finally
        {
            completion.SetResult(resolved);
            await read.WaitAsync(TimeSpan.FromSeconds(2));
        }
        using (policy)
        {
            await context.Posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            context.Run();
            Assert.That(policy!.AllowedAddresses, Is.EqualTo(PeerAddressPermissions.None));
        }
    }

    [Test]
    public async Task Completed_policy_remains_readable_after_resolver_disposal(
        [Values("8.8.8.8", "172.18.0.1", "127.0.0.1")] string external)
    {
        IPResolver resolver = new(new NetworkConfig { ExternalIp = external, EnableExternalIpResolution = false }, LimboLogs.Instance);
        await resolver.Resolve();
        using PeerAddressPolicy policy = new(resolver, new LocalAddressSource(LimboLogs.Instance,
            () => throw new InvalidOperationException("Configured external addresses do not need interfaces.")));
        await resolver.DisposeAsync();
        PeerAddressPermissions expected = external switch
        {
            "172.18.0.1" => PeerAddressPermissions.Private,
            "127.0.0.1" => PeerAddressPermissions.All,
            _ => PeerAddressPermissions.None
        };
        Assert.That(policy.AllowedAddresses, Is.EqualTo(expected));
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Production_feeder_uses_resolved_policy_with_wildcard_listener(
        [Values] bool publicExternal,
        [Values] bool privateInterface,
        CancellationToken token)
    {
        NetworkConfig config = new() { EnableExternalIpResolution = false };
        IIPResolver resolver = Substitute.For<IIPResolver>();
        resolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
            new IIPResolver.NethermindIp(IPAddress.Any, publicExternal ? IPAddress.Parse("8.8.8.8") : IPAddress.None)));
        int enumerations = 0;
        LocalAddressSource addresses = new(LimboLogs.Instance, () =>
        {
            enumerations++;
            return [IPAddress.Loopback, IPAddress.Parse("169.254.1.1"),
                IPAddress.Parse(privateInterface ? "172.18.0.1" : "9.9.9.9")];
        });
        TestNodeSource source = new();
        IDiscoveryApp discovery = Substitute.For<IDiscoveryApp>();
        IProcessExitSource exit = Substitute.For<IProcessExitSource>();
        exit.Token.Returns(token);
        await using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(config))
            .AddSingleton<IIPResolver>(resolver)
            .AddSingleton(addresses)
            .AddSingleton<IDiscoveryApp>(discovery)
            .AddSingleton<IProcessExitSource>(exit)
            .AddKeyedSingleton<INodeSource>(NodeSourceToDiscV4Feeder.SourceKey, source)
            .Build();
        container.Resolve<NetworkListenerState>().SetDiscoveryAddress(IPAddress.Any);
        Node privateNode = new(TestItem.PublicKeyB, "10.1.2.3", 30303);
        Node publicNode = new(TestItem.PublicKeyC, "9.9.9.9", 30303);
        TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        discovery.When(app => app.AddNodeToDiscovery(Arg.Is<Node>(node => node.Id == publicNode.Id)))
            .Do(_ => drained.TrySetResult());
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        exit.Token.Returns(stop.Token);
        Task feeding = container.Resolve<NodeSourceToDiscV4Feeder>().Run();
        try
        {
            source.AddNode(privateNode);
            source.AddNode(publicNode);
            await drained.Task.WaitAsync(token);
            discovery.Received(!publicExternal && privateInterface ? 1 : 0)
                .AddNodeToDiscovery(Arg.Is<Node>(node => node.Id == privateNode.Id));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(container.Resolve<LocalAddressSource>(), Is.SameAs(addresses));
                Assert.That(enumerations, Is.EqualTo(publicExternal ? 0 : 1));
            }
        }
        finally
        {
            await stop.CancelAsync();
            try { await feeding; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    [Test]
    public async Task Production_modules_pass_resolved_external_address_to_persistence([Values(null, "172.18.0.1", "8.8.8.8")] string? external)
    {
        NetworkConfig config = new() { ExternalIp = external, EnableExternalIpResolution = external is null, PeersPersistenceInterval = 10 };
        IPResolver resolver = new(config, LimboLogs.Instance, _ => [new PublicAddressSource()],
            hasLocalAddressFamily: family => family == System.Net.Sockets.AddressFamily.InterNetwork);
        TaskCompletionSource? completedCycle = null;
        await using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(config))
            .AddDecorator<INetworkConfig>((_, network) =>
            {
                network.LocalIp = null;
                network.ExternalIp = external;
                return network;
            })
            .AddSingleton<IIPResolver>(resolver)
            .Map<IEnode, EnodeProvider>(static provider => provider.Enode)
            .AddSingleton<INodeSource>(new TestNodeSource())
            .AddDecorator<INetworkStorage>((_, storage) => new PeerPoolTests.PersistenceHarness.ObservedStorage(storage,
                () => completedCycle?.TrySetResult()))
            .Build();
        IEnode self = container.Resolve<IEnode>();
        Assert.That(self.HostIp, Is.EqualTo(IPAddress.Parse(external ?? "8.8.8.8")));
        IPeerPool pool = container.Resolve<IPeerPool>();
        INetworkStorage storage = container.ResolveKeyed<INetworkStorage>(DbNames.PeersDb);
        Node node = new(TestItem.PublicKeyB, "10.1.2.3", 30303);
        Peer peer = pool.GetOrAdd(node);
        using Session session = new(30312, node, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Handshake(node.Id);
        session.Init(5, Substitute.For<IChannelHandlerContext>(), Substitute.For<IPacketSender>());
        session.MarkAuthenticated();
        session.MarkUsefulSyncResponse();
        peer.OutSession = session;
        completedCycle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.Start();
        try
        {
            await completedCycle.Task.WaitAsync(System.TimeSpan.FromSeconds(2));
            int expected = external == "172.18.0.1" ? 1 : 0;
            Assert.That(storage.GetPersistedNodes().Length, Is.EqualTo(expected));
            NodesLoader loader = container.Resolve<NodesLoader>();
            Assert.That((await loader.DiscoverNodes(default).ToArrayAsync()).Select(candidate => candidate.Id),
                Is.EqualTo(external == "172.18.0.1" ? new[] { node.Id } : []));
        }
        finally
        {
            await pool.StopAsync();
            session.MarkDisconnected(DisconnectReason.ClientQuitting, DisconnectType.Remote, "test cleanup");
        }
    }

    private sealed class PublicAddressSource : IIPSource
    {
        public Task<(bool Success, IPAddress Ip)> TryGetIP(CancellationToken cancellationToken = default)
            => Task.FromResult((true, IPAddress.Parse("8.8.8.8")));
    }

    [Test]
    public async Task Dial_uses_advertised_network_not_listener(
        [Values(null, "8.8.4.4", "10.0.0.1")] string? local,
        [Values("10.0.0.2", "8.8.8.8", "0.0.0.0", "127.0.0.1", "100.64.0.1")] string external,
        [Values("10.1.2.3", "127.0.0.1", "169.254.1.2", "fc00::1", "9.9.9.9")] string target)
    {
        RlpxHostIntegrationTests.FailingClientChannelFactory factory = new();
        IIPResolver resolver = Substitute.For<IIPResolver>();
        resolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
            new IIPResolver.NethermindIp(local is null ? IPAddress.Any : IPAddress.Parse(local), IPAddress.Parse(external))));
        NetworkConfig config = new() { LocalIp = local, P2PPort = RlpxHostIntegrationTests.GetAvailablePort(), ProcessingThreadCount = 1, RlpxHostShutdownCloseTimeoutMs = 100 };
        NetworkListenerState listener = new(IPAddress.Any, IPAddress.Loopback, LimboLogs.Instance);
        await using IContainer container = RlpxHostIntegrationTests.CreateContainer(config, resolver, listener, factory,
            localAddressSource: new LocalAddressSource(LimboLogs.Instance, () => [IPAddress.Parse("172.18.0.1")]));
        RlpxHost host = container.Resolve<RlpxHost>();
        try
        {
            await host.Init();
            await host.ConnectAsync(new Node(TestItem.PublicKeyB, target, 30303));
            Assert.That(factory.CreatedClientChannels, Is.EqualTo(external == "127.0.0.1" || target == "9.9.9.9" || external is "10.0.0.2" or "0.0.0.0" && target is "10.1.2.3" or "fc00::1" ? 1 : 0));
        }
        finally { await host.Shutdown(); }
    }

    [Test]
    public async Task Explicit_private_target_reaches_dialer([Values("static", "trusted", "bootnode")] string privilege)
    {
        RlpxHostIntegrationTests.FailingClientChannelFactory factory = new();
        IIPResolver resolver = Substitute.For<IIPResolver>();
        resolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Parse("8.8.8.8"))));
        NetworkConfig config = new() { P2PPort = RlpxHostIntegrationTests.GetAvailablePort(), ProcessingThreadCount = 1, RlpxHostShutdownCloseTimeoutMs = 100 };
        await using IContainer container = RlpxHostIntegrationTests.CreateContainer(config, resolver,
            new NetworkListenerState(IPAddress.Any, IPAddress.Loopback, LimboLogs.Instance), factory);
        RlpxHost host = container.Resolve<RlpxHost>();
        try
        {
            await host.Init();
            await host.ConnectAsync(new Node(TestItem.PublicKeyB, "10.1.2.3", 30303)
            {
                IsStatic = privilege == "static",
                IsTrusted = privilege == "trusted",
                IsBootnode = privilege == "bootnode"
            });
            Assert.That(factory.CreatedClientChannels, Is.EqualTo(1));
        }
        finally { await host.Shutdown(); }
    }

    [Test]
    public void Wildcard_external_uses_interfaces_only_as_fallback(
        [Values] bool privateInterface, [Values] bool publicExternal,
        [Values("0.0.0.0", "127.0.0.1", "::1", "172.18.0.2", "fc00::2")] string localIp)
    {
        IIPResolver.NethermindIp addresses = new(IPAddress.Parse(localIp), IPAddress.Parse(publicExternal ? "8.8.8.8" : "0.0.0.0"));
        using PeerAddressPolicy policy = new(addresses, new LocalAddressSource(LimboLogs.Instance,
            () => [IPAddress.Loopback, IPAddress.IPv6Loopback, IPAddress.Parse("169.254.1.2"), IPAddress.Parse("fe80::1"),
                IPAddress.Parse(privateInterface ? "172.18.0.1" : "9.9.9.9")]));
        Assert.That(policy.AllowedAddresses, Is.EqualTo(privateInterface && !publicExternal ? PeerAddressPermissions.Private : PeerAddressPermissions.None));
    }

    [Test]
    public void Interface_enumeration_failure_is_retried_and_success_is_cached(
        [Values] bool repeatedFailure, [Values] bool unsupportedPlatform)
    {
        int calls = 0;
        ManualTimeProvider clock = new();
        InterfaceLogger underlying = Substitute.For<InterfaceLogger>();
        underlying.IsWarn.Returns(true);
        ILogManager logs = new OneLoggerLogManager(new ILogger(underlying));
        using PeerAddressPolicy policy = new(new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.None),
            new LocalAddressSource(logs, () =>
            {
                if (++calls <= (repeatedFailure ? 2 : 1))
                {
                    if (unsupportedPlatform) throw new PlatformNotSupportedException();
                    throw new NetworkInformationException();
                }
                return [IPAddress.Parse("172.18.0.1")];
            }, clock));
        Assert.That(policy.AllowedAddresses, Is.EqualTo(PeerAddressPermissions.None));
        for (int index = 0; index < 100; index++) Assert.That(policy.AllowedAddresses, Is.EqualTo(PeerAddressPermissions.None));
        clock.Advance(TimeSpan.FromMilliseconds(59999));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy.AllowedAddresses, Is.EqualTo(PeerAddressPermissions.None));
            Assert.That(calls, Is.EqualTo(1));
        }
        underlying.Received(1).Warn(Arg.Any<string>());
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.That(policy.AllowedAddresses, Is.EqualTo(!repeatedFailure ? PeerAddressPermissions.Private : PeerAddressPermissions.None));
        if (repeatedFailure)
        {
            for (int index = 0; index < 100; index++) Assert.That(policy.AllowedAddresses, Is.EqualTo(PeerAddressPermissions.None));
            Assert.That(calls, Is.EqualTo(2));
            underlying.Received(2).Warn(Arg.Any<string>());
            clock.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.That(policy.AllowedAddresses, Is.EqualTo(PeerAddressPermissions.Private));
        clock.Advance(TimeSpan.FromDays(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy.AllowedAddresses, Is.EqualTo(PeerAddressPermissions.Private));
            Assert.That(calls, Is.EqualTo(repeatedFailure ? 3 : 2));
        }
        underlying.Received(repeatedFailure ? 2 : 1).Warn(Arg.Any<string>());
    }

    [Test]
    public void Logging_failure_does_not_remove_the_interface_retry_deadline()
    {
        int calls = 0;
        ManualTimeProvider clock = new();
        InterfaceLogger underlying = Substitute.For<InterfaceLogger>();
        underlying.IsWarn.Returns(true);
        underlying.When(logger => logger.Warn(Arg.Any<string>()))
            .Do(_ => throw new InvalidOperationException("Logger unavailable."));
        using PeerAddressPolicy policy = new(new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.None),
            new LocalAddressSource(new OneLoggerLogManager(new ILogger(underlying)), () =>
            {
                if (++calls == 1) throw new NetworkInformationException();
                return [IPAddress.Parse("172.18.0.1")];
            }, clock));

        Assert.Throws<InvalidOperationException>(() => _ = policy.AllowedAddresses);
        for (int index = 0; index < 100; index++) Assert.That(policy.AllowedAddresses, Is.EqualTo(PeerAddressPermissions.None));
        Assert.That(calls, Is.EqualTo(1));
        clock.Advance(TimeSpan.FromMinutes(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy.AllowedAddresses, Is.EqualTo(PeerAddressPermissions.Private));
            Assert.That(calls, Is.EqualTo(2));
        }
        underlying.Received(1).Warn(Arg.Any<string>());
    }

    [TestCase("9.9.9.9", false)]
    [TestCase("172.18.0.1", true)]
    [TestCase("fc00::1", true)]
    [TestCase("::ffff:192.168.1.2", true)]
    public async Task Unresolved_external_address_uses_injected_interfaces_for_dialing(string interfaceAddress, bool privateInterface)
    {
        IIPResolver resolver = Substitute.For<IIPResolver>();
        resolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
            new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.None)));
        int scans = 0;
        LocalAddressSource source = new(LimboLogs.Instance, () =>
        {
            scans++;
            return [IPAddress.Loopback, IPAddress.IPv6Loopback, IPAddress.Parse("169.254.1.2"), IPAddress.Parse("fe80::1"),
                IPAddress.Parse(interfaceAddress)];
        });
        RlpxHostIntegrationTests.FailingClientChannelFactory factory = new();
        NetworkConfig config = new() { P2PPort = RlpxHostIntegrationTests.GetAvailablePort(), ProcessingThreadCount = 1, RlpxHostShutdownCloseTimeoutMs = 100 };
        await using IContainer container = RlpxHostIntegrationTests.CreateContainer(config, resolver,
            new NetworkListenerState(IPAddress.Any, IPAddress.Loopback, LimboLogs.Instance), factory, localAddressSource: source);
        RlpxHost host = container.Resolve<RlpxHost>();
        try
        {
            await host.Init();
            await host.ConnectAsync(new Node(TestItem.PublicKeyB, "10.1.2.3", 30303));
            await host.ConnectAsync(new Node(TestItem.PublicKeyC, "127.0.0.2", 30303));
            await host.ConnectAsync(new Node(TestItem.PublicKeyD, "169.254.1.2", 30303));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(factory.CreatedClientChannels, Is.EqualTo(privateInterface ? 1 : 0));
                Assert.That(container.Resolve<LocalAddressSource>(), Is.SameAs(source));
                Assert.That(scans, Is.EqualTo(1));
            }
        }
        finally { await host.Shutdown(); }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Production_modules_share_unresolved_address_policy_for_persistence(
        [Values] bool privateInterface, CancellationToken token)
    {
        NetworkConfig config = new() { PeersPersistenceInterval = 10 };
        IIPResolver resolver = Substitute.For<IIPResolver>();
        resolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
            new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.None)));
        int scans = 0;
        LocalAddressSource interfaces = new(LimboLogs.Instance, () =>
        {
            scans++;
            return [IPAddress.Loopback, IPAddress.Parse("169.254.1.2"), IPAddress.Parse("fe80::1"),
                IPAddress.Parse(privateInterface ? "172.18.0.1" : "8.8.8.8")];
        });
        TaskCompletionSource? completedCycle = null;
        await using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(config))
            .AddSingleton<IIPResolver>(resolver)
            .AddSingleton(interfaces)
            .AddSingleton<INodeSource>(new TestNodeSource())
            .AddDecorator<INetworkStorage>((_, storage) => new PeerPoolTests.PersistenceHarness.ObservedStorage(storage,
                () => completedCycle?.TrySetResult()))
            .Build();
        PeerAddressPolicy policy = container.Resolve<PeerAddressPolicy>();
        IPeerPool pool = container.Resolve<IPeerPool>();
        INetworkStorage storage = container.ResolveKeyed<INetworkStorage>(DbNames.PeersDb);
        Node node = new(TestItem.PublicKeyB, "10.1.2.3", 30303);
        Peer peer = pool.GetOrAdd(node);
        using Session session = new(30312, node, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Handshake(node.Id);
        session.Init(5, Substitute.For<IChannelHandlerContext>(), Substitute.For<IPacketSender>());
        session.MarkAuthenticated();
        session.MarkUsefulSyncResponse();
        peer.OutSession = session;
        completedCycle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.Start();
        try
        {
            await completedCycle.Task.WaitAsync(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(storage.GetPersistedNodes().Length, Is.EqualTo(privateInterface ? 1 : 0));
                Assert.That(policy.AllowedAddresses, Is.EqualTo(privateInterface ? PeerAddressPermissions.Private : PeerAddressPermissions.None));
                Assert.That(scans, Is.EqualTo(1));
            }
            NodesLoader loader = container.Resolve<NodesLoader>();
            Assert.That((await loader.DiscoverNodes(token).ToArrayAsync()).Select(candidate => candidate.Id),
                Is.EqualTo(privateInterface ? new[] { node.Id } : []));
        }
        finally
        {
            await pool.StopAsync();
            session.MarkDisconnected(DisconnectReason.ClientQuitting, DisconnectType.Remote, "test cleanup");
        }
    }

    [Test]
    public async Task Persistence_and_reload_follow_external_network(
        [Values("172.18.0.1", "8.8.8.8")] string external,
        [Values("10.1.2.3", "127.0.0.1", "169.254.1.2", "fc00::1", "9.9.9.9")] string target)
    {
        IEnode self = new Enode(TestItem.PublicKeyA, IPAddress.Parse(external), 30303);
        NetworkConfig config = new();
        await using PeerPoolTests.PersistenceHarness test = new(config, enode: self);
        Node peer = new(TestItem.PublicKeyB, target, 30303);
        test.AddSessionPeer(peer);
        await test.Tick();
        bool expected = external == "172.18.0.1" && target is "10.1.2.3" or "fc00::1" || target == "9.9.9.9";
        Assert.That(test.Storage.GetPersistedNodes().Length, Is.EqualTo(expected ? 1 : 0));
        NodesLoader loader = new(config, test.Stats, test.Storage, self, LimboLogs.Instance, new NodesLoaderOptions(), test.AddressPolicy);
        Node[] loaded = await loader.DiscoverNodes(default).ToArrayAsync();
        Assert.That(loaded.Select(node => node.Id), Is.EqualTo(expected ? new[] { peer.Id } : []));
    }

    [Test]
    public async Task Policy_mismatch_skips_stored_private_peer_without_erasing_it()
    {
        IEnode self = new Enode(TestItem.PublicKeyA, IPAddress.Parse("8.8.8.8"), 30303);
        await using PeerPoolTests.PersistenceHarness test = new(enode: self);
        test.Storage.UpdateNode(new NetworkNode(TestItem.PublicKeyB, "10.1.2.3", 30303));
        await test.Tick();
        NodesLoader loader = new(new NetworkConfig(), test.Stats, test.Storage, self, LimboLogs.Instance, new NodesLoaderOptions(), test.AddressPolicy);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await loader.DiscoverNodes(default).ToArrayAsync(), Is.Empty);
            Assert.That(test.Storage.GetPersistedNodes().Select(node => node.NodeId), Is.EqualTo(new[] { TestItem.PublicKeyB }));
        }
    }
}
