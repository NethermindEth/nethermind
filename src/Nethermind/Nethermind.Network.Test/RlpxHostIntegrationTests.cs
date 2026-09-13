// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[NonParallelizable]
[TestFixture]
public class RlpxHostIntegrationTests
{
    [TestCase(true, false, null, "203.0.113.1", "203.0.113.1", false, Description = "Exact match: blocks same IP")]
    [TestCase(true, false, null, "203.0.113.1", "198.51.100.1", true, Description = "Exact match: allows different IP")]
    [TestCase(true, true, null, "203.0.113.1", "203.0.113.50", false, Description = "Subnet bucketing: blocks same subnet")]
    [TestCase(false, false, null, "203.0.113.1", "203.0.113.1", true, Description = "Filtering disabled: always accepts")]
    [TestCase(true, true, "192.168.1.100", "192.168.1.10", "192.168.1.20", true, Description = "Same local subnet uses exact matching")]
    [TestCase(true, true, "203.0.113.100", "192.168.1.1", "192.168.1.2", true, Description = "Private remote IP uses exact matching")]
    public async Task ShouldContact_FiltersCorrectly(bool filterEnabled, bool subnetBucketing, string? externalIp,
        string addr1, string addr2, bool secondExpected)
    {
        await using IContainer container = CreateFilterContainer(filterEnabled, subnetBucketing, externalIp);
        RlpxHost host = container.Resolve<RlpxHost>();
        try
        {
            Assert.That(host.ShouldContact(IPAddress.Parse(addr1)), Is.True, "first IP should be accepted");
            Assert.That(host.ShouldContact(IPAddress.Parse(addr2)), Is.EqualTo(secondExpected));
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task TrackSessionActivity_RefreshesFilterOnReceivedAndDeliveredMessages()
    {
        await using IContainer container = CreateFilterContainer(filterEnabled: true, subnetBucketing: true);
        RlpxHost host = container.Resolve<RlpxHost>();
        try
        {
            IPAddress receivedIp = IPAddress.Parse("203.0.113.1");
            ISession receivedSession = Substitute.For<ISession>();
            receivedSession.Node.Returns(new Node(TestItem.PublicKeyA, receivedIp.ToString(), 30303));

            host.TrackSessionActivity(receivedSession);
            receivedSession.MsgReceived += Raise.EventWith(receivedSession, new PeerEventArgs(receivedSession.Node, "eth", 1, 32));

            Assert.That(host.ShouldContact(receivedIp), Is.False, "received traffic should keep the active session filtered");

            IPAddress deliveredIp = IPAddress.Parse("198.51.100.1");
            ISession deliveredSession = Substitute.For<ISession>();
            deliveredSession.Node.Returns(new Node(TestItem.PublicKeyA, deliveredIp.ToString(), 30303));

            host.TrackSessionActivity(deliveredSession);
            deliveredSession.MsgDelivered += Raise.EventWith(deliveredSession, new PeerEventArgs(deliveredSession.Node, "eth", 2, 64));

            Assert.That(host.ShouldContact(deliveredIp), Is.False, "sent traffic should keep the active session filtered");
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task ShouldContact_AlwaysAcceptsPrivilegedIp()
    {
        IPAddress privilegedIp = IPAddress.Parse("203.0.113.1");
        IPrivilegedIpProvider privilegedIpProvider = Substitute.For<IPrivilegedIpProvider>();
        privilegedIpProvider.IsPrivileged(privilegedIp).Returns(true);

        // Exact-match filtering would otherwise block the second attempt from the same IP.
        await using IContainer container = CreateFilterContainer(filterEnabled: true, subnetBucketing: false, privilegedIpProvider: privilegedIpProvider);
        RlpxHost host = container.Resolve<RlpxHost>();
        try
        {
            Assert.That(host.ShouldContact(privilegedIp), Is.True, "first attempt accepted");
            Assert.That(host.ShouldContact(privilegedIp), Is.True, "privileged IP is never rate-limited");
        }
        finally
        {
            await host.Shutdown();
        }
    }

    private static IEnumerable<TestCaseData> InboundBindAddressCases()
    {
        yield return new TestCaseData("0.0.0.0", null, true, "::");
        yield return new TestCaseData("0.0.0.0", "0.0.0.0", true, "0.0.0.0");
        yield return new TestCaseData("0.0.0.0", "0", true, "0.0.0.0");
        yield return new TestCaseData("0.0.0.0", " 0.0.0.0 ", true, "0.0.0.0");
        yield return new TestCaseData("::", null, true, "::");
        yield return new TestCaseData("::", "::", true, "::");
        yield return new TestCaseData("127.0.0.1", null, true, "127.0.0.1");
        yield return new TestCaseData("192.168.1.5", null, true, "192.168.1.5");
        yield return new TestCaseData("192.168.1.5", "192.168.1.5", true, "192.168.1.5");
        yield return new TestCaseData("2001:db8::1", null, true, "2001:db8::1");
        yield return new TestCaseData("2001:db8::1", null, false, "2001:db8::1");
        yield return new TestCaseData("0.0.0.0", null, false, "0.0.0.0");
        yield return new TestCaseData("::", null, false, "::");
    }

    [Parallelizable(ParallelScope.Self)]
    [TestCaseSource(nameof(InboundBindAddressCases))]
    public void GetInboundBindAddress_honors_explicit_configuration_and_dual_stack_support(string localIp, string? localIpConfig, bool supportsDualStack, string expectedIp)
    {
        IPAddress result = NetworkHelper.GetInboundBindAddress(IPAddress.Parse(localIp), localIpConfig, supportsDualStack);

        Assert.That(result, Is.EqualTo(IPAddress.Parse(expectedIp)));
    }

    [Test]
    [Parallelizable(ParallelScope.Self)]
    public void GetInboundBindAddress_uses_automatic_dual_stack_only_where_wildcard_bind_is_exclusive()
    {
        IPAddress expected = (OperatingSystem.IsMacOS(), Socket.OSSupportsIPv6) switch
        {
            // macOS wildcards can share a port, so a successful IPv6 bind does not prove IPv4 ownership.
            (true, _) => IPAddress.Any,
            (false, false) => IPAddress.Any,
            (false, true) => IPAddress.IPv6Any
        };

        Assert.That(NetworkHelper.GetInboundBindAddress(IPAddress.Any, null), Is.EqualTo(expected));
    }

    [Parallelizable(ParallelScope.Self)]
    [TestCase("::ffff:192.168.1.5", "192.168.1.5")]
    [TestCase("::ffff:7f00:1", "127.0.0.1")]
    [TestCase("2001:db8::1", "2001:db8::1")]
    [TestCase("127.0.0.1", "127.0.0.1")]
    public void NormalizeMappedIPv4_reduces_mapped_addresses_to_ipv4(string input, string expectedIp)
        => Assert.That(IPAddress.Parse(input).NormalizeMappedIPv4(), Is.EqualTo(IPAddress.Parse(expectedIp)));

    [Test]
    public void TryGetLocalIPEndpoint_UsesChannelEndpointSourceAsFallback()
    {
        IPEndPoint expected = new(IPAddress.Loopback, 30303);
        IChannel channel = Substitute.For<IChannel, IIPEndpointSource>();
        ((IIPEndpointSource)channel).IPEndpoint.Returns(expected);

        Assert.That(channel.TryGetLocalIPEndpoint(), Is.SameAs(expected));
    }

    [TestCase("0.0.0.0", "0.0.0.0", true, false)]
    [TestCase("127.0.0.1", "127.0.0.1", true, false)]
    [TestCase("::1", "::1", false, true)]
    [TestCase("::", "::", true, true)]
    public async Task Listener_HonorsExplicitAddressFamily(
        string configuredIp,
        string expectedBoundIp,
        bool acceptsIpv4,
        bool acceptsIpv6)
    {
        if (IPAddress.Parse(configuredIp).AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host.");
        }

        int port = GetAvailablePort();
        await using IContainer container = CreateListenerContainer(configuredIp, IPAddress.Parse(configuredIp), port);
        RlpxHost host = container.Resolve<RlpxHost>();
        NetworkListenerState listenerState = container.Resolve<NetworkListenerState>();
        try
        {
            await host.Init();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(listenerState.RlpxAddress, Is.EqualTo(IPAddress.Parse(expectedBoundIp)));
                Assert.That(await CanConnect(AddressFamily.InterNetwork, port), Is.EqualTo(acceptsIpv4));
                Assert.That(await CanConnect(AddressFamily.InterNetworkV6, port), Is.EqualTo(acceptsIpv6));
            }
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task DefaultListener_UsesSupportedFamiliesAndNormalizesIpv4Session()
    {
        int port = GetAvailablePort();
        IPrivilegedIpProvider privilegedIpProvider = Substitute.For<IPrivilegedIpProvider>();
        await using IContainer container = CreateListenerContainer(
            null,
            IPAddress.Any,
            port,
            privilegedIpProvider: privilegedIpProvider);
        RlpxHost host = container.Resolve<RlpxHost>();
        NetworkListenerState listenerState = container.Resolve<NetworkListenerState>();
        TaskCompletionSource<string> remoteHost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SessionCreated += (_, args) => remoteHost.TrySetResult(args.Session.RemoteHost);
        bool acceptsIpv6 = Socket.OSSupportsIPv6 && !OperatingSystem.IsMacOS();
        try
        {
            await host.Init();

            Assert.That(listenerState.RlpxAddress, Is.EqualTo(acceptsIpv6 ? IPAddress.IPv6Any : IPAddress.Any));
            Assert.That(await CanConnect(AddressFamily.InterNetwork, port), Is.True);
            Assert.That(await remoteHost.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IPAddress.Loopback.ToString()));
            privilegedIpProvider.Received().IsPrivileged(Arg.Is<IPAddress>(ip => ip.Equals(IPAddress.Loopback)));
            Assert.That(await CanConnect(AddressFamily.InterNetworkV6, port), Is.EqualTo(acceptsIpv6));
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task DefaultListener_FallsBackToIpv4WhenWidenedBindFails([Values] bool portInUse)
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host.");
        }

        using Socket? ipv6Blocker = portInUse ? CreateTcpListenerSocket(IPAddress.IPv6Any, 0) : null;
        int port = ipv6Blocker is not null ? ((IPEndPoint)ipv6Blocker.LocalEndPoint!).Port : GetAvailablePort();
        NetworkListenerState listenerState = new(IPAddress.Any, IPAddress.IPv6Any, LimboLogs.Instance);
        Ipv4ServerChannelFactory? channelFactory = portInUse ? null : new();
        InterfaceLogger underlyingLogger = Substitute.For<InterfaceLogger>();
        underlyingLogger.IsWarn.Returns(true);
        await using IContainer container = CreateListenerContainer(null, IPAddress.Any, port, listenerState, channelFactory,
            logManager: new OneLoggerLogManager(new ILogger(underlyingLogger)));
        RlpxHost host = container.Resolve<RlpxHost>();
        try
        {
            await host.Init();

            Assert.That(listenerState.RlpxAddress, Is.EqualTo(IPAddress.Any));
            Assert.That(await CanConnect(AddressFamily.InterNetwork, port), Is.True);
            underlyingLogger.Received(1).Warn(Arg.Is<string>(message =>
                message.StartsWith("Failed to bind RlpxHost") && message.Contains(typeof(SocketException).FullName!)));
            if (channelFactory is not null)
            {
                Assert.That(channelFactory.CreatedChannels, Has.Count.EqualTo(2));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(channelFactory.CreatedChannels[0].Open, Is.False);
                    Assert.That(channelFactory.CreatedChannels[0].CloseCompletion.IsCompletedSuccessfully, Is.True);
                }
            }
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task DefaultListener_SurfacesCollisionOnFallbackAndReleasesFailedChannels()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host.");
        }

        int port;
        using (Socket ipv4Blocker = CreateTcpListenerSocket(IPAddress.Any, 0))
        {
            port = ((IPEndPoint)ipv4Blocker.LocalEndPoint!).Port;
            NetworkListenerState listenerState = new(IPAddress.Any, IPAddress.IPv6Any, LimboLogs.Instance);
            Ipv4ServerChannelFactory channelFactory = new();
            await using IContainer container = CreateListenerContainer(null, IPAddress.Any, port, listenerState, channelFactory);
            RlpxHost host = container.Resolve<RlpxHost>();
            try
            {
                Assert.That(async () => await host.Init(), Throws.TypeOf<PortInUseException>());
                Assert.That(channelFactory.CreatedChannels, Has.Count.EqualTo(2));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(listenerState.RlpxAddress, Is.Null);
                    AssertChannelsClosed(channelFactory.CreatedChannels);
                }
            }
            finally
            {
                await host.Shutdown();
            }
        }

        using Socket releasedIpv4 = CreateTcpListenerSocket(IPAddress.Any, port);
    }

    [TestCase(null, "0.0.0.0", "0.0.0.0", false, Description = "Default listener with a reuse-enabled IPv4 blocker")]
    [TestCase(null, "0.0.0.0", "0.0.0.0", true, Description = "Default listener with an exclusive IPv4 blocker")]
    [TestCase("127.0.0.1", "127.0.0.1", "127.0.0.1", false, Description = "Explicit IPv4 listener")]
    [TestCase("::1", "::1", "::1", false, Description = "Explicit IPv6 listener")]
    [TestCase("::", "::", "::", true, Description = "Explicit dual-stack listener")]
    public async Task Listener_SurfacesCollision(
        string? configuredIp,
        string listenerAddressText,
        string blockerAddressText,
        bool blockerExclusiveAddressUse)
    {
        IPAddress listenerAddress = IPAddress.Parse(listenerAddressText);
        IPAddress blockerAddress = IPAddress.Parse(blockerAddressText);
        if ((listenerAddress.AddressFamily == AddressFamily.InterNetworkV6 || blockerAddress.AddressFamily == AddressFamily.InterNetworkV6) &&
            !Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host.");
        }

        int port;
        using (Socket blocker = CreateTcpListenerSocket(blockerAddress, 0, blockerExclusiveAddressUse))
        {
            port = ((IPEndPoint)blocker.LocalEndPoint!).Port;
            await using IContainer container = CreateListenerContainer(configuredIp, listenerAddress, port);
            RlpxHost host = container.Resolve<RlpxHost>();
            NetworkListenerState listenerState = container.Resolve<NetworkListenerState>();
            try
            {
                Assert.That(async () => await host.Init(), Throws.TypeOf<PortInUseException>());
                Assert.That(listenerState.RlpxAddress, Is.Null);
            }
            finally
            {
                await host.Shutdown();
            }
        }

        using Socket released = CreateTcpListenerSocket(blockerAddress, port);
    }

    [Test]
    public async Task ListenerStateSubscriberFailure_DoesNotAffectBindOrShutdown()
    {
        int port = GetAvailablePort();
        NetworkListenerState listenerState = new(IPAddress.Any, IPAddress.Any, LimboLogs.Instance);
        listenerState.Changed += (_, _) => throw new InvalidOperationException("subscriber failure");
        await using IContainer container = CreateListenerContainer("0.0.0.0", IPAddress.Any, port, listenerState);
        RlpxHost host = container.Resolve<RlpxHost>();
        bool shutDown = false;
        try
        {
            await host.Init();
            Assert.That(listenerState.RlpxAddress, Is.EqualTo(IPAddress.Any));

            await host.Shutdown();
            shutDown = true;
            Assert.That(listenerState.RlpxAddress, Is.Null);
        }
        finally
        {
            if (!shutDown)
            {
                await host.Shutdown();
            }
        }
    }

    [Test]
    public async Task ListenerState_ClearsWhenChannelClosesUnexpectedly()
    {
        int port = GetAvailablePort();
        NetworkListenerState listenerState = new(IPAddress.Any, IPAddress.Any, LimboLogs.Instance);
        Ipv4ServerChannelFactory channelFactory = new();
        await using IContainer container = CreateListenerContainer("0.0.0.0", IPAddress.Any, port, listenerState, channelFactory);
        RlpxHost host = container.Resolve<RlpxHost>();
        try
        {
            await host.Init();
            TaskCompletionSource cleared = new(TaskCreationOptions.RunContinuationsAsynchronously);
            listenerState.Changed += (_, _) =>
            {
                if (listenerState.RlpxAddress is null) cleared.TrySetResult();
            };

            Assert.That(channelFactory.CreatedChannels, Has.Count.EqualTo(1));
            await channelFactory.CreatedChannels[0].CloseAsync();
            await cleared.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(listenerState.RlpxAddress, Is.Null);
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task ListenerState_DoesNotClearReplacementWhenPreviousChannelCloses([Values] bool rlpx, [Values] bool sameAddress)
    {
        NetworkListenerState listenerState = new(IPAddress.Any, IPAddress.Any, LimboLogs.Instance);
        TaskCompletionSource closeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IPAddress replacementAddress = sameAddress ? IPAddress.Any : IPAddress.IPv6Any;
        Task closeObserver;
        if (rlpx)
        {
            closeObserver = listenerState.TrackRlpxAddress(IPAddress.Any, closeCompletion.Task);
            listenerState.SetRlpxAddress(replacementAddress);
        }
        else
        {
            closeObserver = listenerState.TrackDiscoveryAddress(IPAddress.Any, closeCompletion.Task);
            listenerState.SetDiscoveryAddress(replacementAddress);
        }

        closeCompletion.SetResult();
        await closeObserver;

        Assert.That(
            rlpx ? listenerState.RlpxAddress : listenerState.DiscoveryAddress,
            Is.EqualTo(replacementAddress));
    }

    private static IContainer CreateFilterContainer(bool filterEnabled, bool subnetBucketing, string? externalIp = null,
        IPrivilegedIpProvider? privilegedIpProvider = null)
    {
        NetworkConfig networkConfig = new()
        {
            ProcessingThreadCount = 1,
            P2PPort = GetAvailablePort(),
            FilterPeersByRecentIp = filterEnabled,
            FilterPeersBySameSubnet = subnetBucketing,
            ExternalIp = externalIp,
            MaxActivePeers = 50
        };

        IIPResolver ipResolver = Substitute.For<IIPResolver>();
        ipResolver.Resolve(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IIPResolver.NethermindIp>(new IIPResolver.NethermindIp(IPAddress.Loopback, externalIp is null ? IPAddress.None : IPAddress.Parse(externalIp))));

        return CreateContainer(networkConfig, ipResolver, privilegedIpProvider: privilegedIpProvider);
    }

    private static IContainer CreateListenerContainer(
        string? localIpConfig,
        IPAddress resolvedLocalIp,
        int port,
        NetworkListenerState? listenerState = null,
        IChannelFactory? channelFactory = null,
        IPrivilegedIpProvider? privilegedIpProvider = null,
        ILogManager? logManager = null)
    {
        NetworkConfig networkConfig = new()
        {
            ProcessingThreadCount = 1,
            P2PPort = port,
            LocalIp = localIpConfig,
            MaxActivePeers = 50,
            RlpxHostShutdownCloseTimeoutMs = 100
        };
        IIPResolver ipResolver = Substitute.For<IIPResolver>();
        ipResolver.Resolve(Arg.Any<CancellationToken>()).Returns(
            new ValueTask<IIPResolver.NethermindIp>(new IIPResolver.NethermindIp(resolvedLocalIp, IPAddress.Loopback)));
        return CreateContainer(networkConfig, ipResolver, listenerState, channelFactory, privilegedIpProvider, logManager);
    }

    private static IContainer CreateContainer(
        INetworkConfig networkConfig,
        IIPResolver ipResolver,
        NetworkListenerState? listenerState = null,
        IChannelFactory? channelFactory = null,
        IPrivilegedIpProvider? privilegedIpProvider = null,
        ILogManager? logManager = null)
    {
        ContainerBuilder builder = new();
        builder.RegisterModule(new NetworkModule(new ConfigProvider(networkConfig)));
        builder.RegisterInstance(networkConfig);
        builder.RegisterInstance(ipResolver);
        builder.RegisterInstance(logManager ?? LimboLogs.Instance).As<ILogManager>();
        builder.RegisterInstance(Substitute.For<IMessageSerializationService>());
        builder.RegisterInstance(Substitute.For<IHandshakeService>());
        builder.RegisterInstance(Substitute.For<ISessionMonitor>());
        builder.RegisterInstance(NullDisconnectsAnalyzer.Instance).As<IDisconnectsAnalyzer>();
        builder.RegisterInstance(privilegedIpProvider ?? Substitute.For<IPrivilegedIpProvider>());
        if (listenerState is not null)
        {
            builder.RegisterInstance(listenerState);
        }

        if (channelFactory is not null)
        {
            builder.RegisterInstance(channelFactory);
        }

        return builder.Build();
    }

    private static async Task<bool> CanConnect(AddressFamily addressFamily, int port)
    {
        if (addressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
        {
            return false;
        }

        using Socket socket = new(addressFamily, SocketType.Stream, ProtocolType.Tcp);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        try
        {
            IPAddress address = addressFamily == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback;
            await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token);
            return true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static void AssertChannelsClosed(IReadOnlyList<IChannel> channels)
    {
        foreach (IChannel channel in channels)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(channel.Open, Is.False);
                Assert.That(channel.CloseCompletion.IsCompletedSuccessfully, Is.True);
            }
        }
    }

    private static Socket CreateTcpListenerSocket(IPAddress address, int port, bool exclusiveAddressUse = true)
    {
        Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.ExclusiveAddressUse = exclusiveAddressUse;
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                socket.DualMode = false;
            }

            socket.Bind(new IPEndPoint(address, port));
            socket.Listen();
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class Ipv4ServerChannelFactory : IChannelFactory
    {
        public List<IServerChannel> CreatedChannels { get; } = [];

        public IServerChannel CreateServer()
        {
            IServerChannel channel = new TcpServerSocketChannel(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp));
            CreatedChannels.Add(channel);
            return channel;
        }

        public IChannel CreateClient() => new TcpSocketChannel();

        public IChannel CreateDatagramChannel() => new SocketDatagramChannel();
    }

    private static int GetAvailablePort()
    {
        using System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
