// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Core.Test.Builders;
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
    [TestCase("0.0.0.0")]
    [TestCase("127.0.0.1")]
    [TestCase("::1")]
    public void SingleFamilyServerSocket_DoesNotRequireExclusiveAddressUse(string addressText)
    {
        IPAddress address = IPAddress.Parse(addressText);
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host.");
        }

        using Socket socket = RlpxHost.CreateServerSocket(address);

        Assert.That(socket.ExclusiveAddressUse, Is.False);
    }

    [TestCase(true, false, null, "203.0.113.1", "203.0.113.1", false, Description = "Exact match: blocks same IP")]
    [TestCase(true, false, null, "203.0.113.1", "198.51.100.1", true, Description = "Exact match: allows different IP")]
    [TestCase(true, true, null, "203.0.113.1", "203.0.113.50", false, Description = "Subnet bucketing: blocks same subnet")]
    [TestCase(false, false, null, "203.0.113.1", "203.0.113.1", true, Description = "Filtering disabled: always accepts")]
    [TestCase(true, true, "192.168.1.100", "192.168.1.10", "192.168.1.20", true, Description = "Same local subnet uses exact matching")]
    [TestCase(true, true, "203.0.113.100", "192.168.1.1", "192.168.1.2", true, Description = "Private remote IP uses exact matching")]
    public async Task ShouldContact_FiltersCorrectly(bool filterEnabled, bool subnetBucketing, string? externalIp,
        string addr1, string addr2, bool secondExpected)
    {
        RlpxHost host = CreateHost(filterEnabled, subnetBucketing, externalIp);
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
        RlpxHost host = CreateHost(filterEnabled: true, subnetBucketing: true);
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
        RlpxHost host = CreateHost(filterEnabled: true, subnetBucketing: false, privilegedIpProvider: privilegedIpProvider);
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

        if (configuredIp == "::" && OperatingSystem.IsMacOS())
        {
            acceptsIpv4 = false;
        }

        int port = GetAvailablePort();
        (RlpxHost host, NetworkListenerState listenerState) = CreateListenerHost(configuredIp, IPAddress.Parse(configuredIp), port);
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
        (RlpxHost host, NetworkListenerState listenerState) = CreateListenerHost(
            null,
            IPAddress.Any,
            port,
            privilegedIpProvider: privilegedIpProvider);
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
    public async Task DefaultListener_FallsBackToIpv4WhenWidenedBindFails()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host.");
        }

        int port = GetAvailablePort();
        NetworkListenerState listenerState = new(IPAddress.Any, IPAddress.IPv6Any, LimboLogs.Instance);
        Ipv4ServerChannelFactory channelFactory = new();
        (RlpxHost host, _) = CreateListenerHost(null, IPAddress.Any, port, listenerState, channelFactory);
        try
        {
            await host.Init();

            Assert.That(listenerState.RlpxAddress, Is.EqualTo(IPAddress.Any));
            Assert.That(await CanConnect(AddressFamily.InterNetwork, port), Is.True);
            Assert.That(channelFactory.CreatedChannels, Has.Count.EqualTo(2));
            Assert.That(channelFactory.CreatedChannels[0].Open, Is.False);
            Assert.That(channelFactory.CreatedChannels[0].CloseCompletion.IsCompletedSuccessfully, Is.True);
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
            (RlpxHost host, _) = CreateListenerHost(null, IPAddress.Any, port, listenerState, channelFactory);

            Assert.That(async () => await host.Init(), Throws.TypeOf<PortInUseException>());
            Assert.That(listenerState.RlpxAddress, Is.Null);
            Assert.That(channelFactory.CreatedChannels, Has.Count.EqualTo(2));
            AssertChannelsClosed(channelFactory.CreatedChannels);
        }

        using Socket releasedIpv4 = CreateTcpListenerSocket(IPAddress.Any, port);
    }

    [Test]
    public async Task DefaultListener_DoesNotClaimDualStackWhenIpv4PortIsOccupied()
    {
        int port;
        using (Socket blocker = CreateTcpListenerSocket(IPAddress.Any, 0))
        {
            port = ((IPEndPoint)blocker.LocalEndPoint!).Port;
            (RlpxHost host, NetworkListenerState listenerState) = CreateListenerHost(null, IPAddress.Any, port);

            Assert.That(async () => await host.Init(), Throws.TypeOf<PortInUseException>());
            Assert.That(listenerState.RlpxAddress, Is.Null);
        }

        using Socket released = CreateTcpListenerSocket(IPAddress.Any, port);
    }

    [Test]
    public async Task ListenerStateSubscriberFailure_DoesNotAffectBindOrShutdown()
    {
        int port = GetAvailablePort();
        NetworkListenerState listenerState = new(IPAddress.Any, IPAddress.Any, LimboLogs.Instance);
        listenerState.Changed += (_, _) => throw new InvalidOperationException("subscriber failure");
        (RlpxHost host, _) = CreateListenerHost("0.0.0.0", IPAddress.Any, port, listenerState);
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
        (RlpxHost host, _) = CreateListenerHost("0.0.0.0", IPAddress.Any, port, listenerState, channelFactory);
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

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task ListenerState_DoesNotClearReplacementWhenPreviousChannelCloses(bool rlpx, bool sameAddress)
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

    private static RlpxHost CreateHost(bool filterEnabled, bool subnetBucketing, string? externalIp = null,
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

        return new RlpxHost(
            Substitute.For<IMessageSerializationService>(),
            Substitute.For<IHandshakeService>(),
            Substitute.For<ISessionMonitor>(),
            NullDisconnectsAnalyzer.Instance,
            networkConfig,
            ipResolver,
            privilegedIpProvider ?? Substitute.For<IPrivilegedIpProvider>(),
            LimboLogs.Instance,
            new NetworkListenerState(networkConfig, ipResolver, LimboLogs.Instance));
    }

    private static (RlpxHost Host, NetworkListenerState ListenerState) CreateListenerHost(
        string? localIpConfig,
        IPAddress resolvedLocalIp,
        int port,
        NetworkListenerState? listenerState = null,
        IChannelFactory? channelFactory = null,
        IPrivilegedIpProvider? privilegedIpProvider = null)
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
        listenerState ??= new NetworkListenerState(networkConfig, ipResolver, LimboLogs.Instance);
        RlpxHost host = new(
            Substitute.For<IMessageSerializationService>(),
            Substitute.For<IHandshakeService>(),
            Substitute.For<ISessionMonitor>(),
            NullDisconnectsAnalyzer.Instance,
            networkConfig,
            ipResolver,
            privilegedIpProvider ?? Substitute.For<IPrivilegedIpProvider>(),
            LimboLogs.Instance,
            listenerState,
            channelFactory);
        return (host, listenerState);
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
        using (Assert.EnterMultipleScope())
        {
            foreach (IChannel channel in channels)
            {
                Assert.That(channel.Open, Is.False);
                Assert.That(channel.CloseCompletion.IsCompletedSuccessfully, Is.True);
            }
        }
    }

    private static Socket CreateTcpListenerSocket(IPAddress address, int port)
    {
        Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.ExclusiveAddressUse = true;
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
