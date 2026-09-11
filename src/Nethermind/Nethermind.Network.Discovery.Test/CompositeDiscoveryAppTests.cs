// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

public class CompositeDiscoveryAppTests
{
    [TestCase("0.0.0.0", AddressFamily.InterNetwork, false)]
    [TestCase("127.0.0.1", AddressFamily.InterNetwork, false)]
    [TestCase("::1", AddressFamily.InterNetworkV6, false)]
    [TestCase("::", AddressFamily.InterNetworkV6, true)]
    [TestCase("::ffff:0.0.0.0", AddressFamily.InterNetworkV6, true)]
    public void CreateDatagramSocket_MatchesListenerAddress(string localIp, AddressFamily expectedFamily, bool expectedDualMode)
    {
        using Socket socket = CompositeDiscoveryApp.CreateDatagramSocket(IPAddress.Parse(localIp));

        Assert.That(socket.AddressFamily, Is.EqualTo(expectedFamily));
        if (expectedFamily == AddressFamily.InterNetworkV6)
        {
            Assert.That(socket.DualMode, Is.EqualTo(expectedDualMode));
        }
    }

    [Test]
    [NonParallelizable]
    public async Task StartAsync_ReleasesChannelAndEventLoopWhenBindFails()
    {
        int port;
        using (Socket blocker = CreateUdpListenerSocket(IPAddress.Any, 0))
        {
            port = ((IPEndPoint)blocker.LocalEndPoint!).Port;
            NetworkConfig networkConfig = new() { LocalIp = "0.0.0.0", DiscoveryPort = port };
            IIPResolver ipResolver = Substitute.For<IIPResolver>();
            ipResolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
                new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Loopback)));
            NetworkListenerState listenerState = new(networkConfig, ipResolver, LimboLogs.Instance);
            IDiscoveryApp discoveryApp = Substitute.For<IDiscoveryApp>();
            discoveryApp.StopAsync().Returns(Task.CompletedTask);
            RecordingChannelFactory channelFactory = new();
            CompositeDiscoveryApp app = new(
                networkConfig,
                new DiscoveryConfig(),
                LimboLogs.Instance,
                listenerState,
                [discoveryApp],
                channelFactory);

            Assert.That(async () => await app.StartAsync(), Throws.TypeOf<PortInUseException>());

            Assert.That(channelFactory.CreatedChannels, Has.Count.EqualTo(1));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(app.HasEventLoopGroup, Is.False);
                Assert.That(listenerState.DiscoveryAddress, Is.Null);
                AssertChannelsClosed(channelFactory.CreatedChannels);
            }
            await discoveryApp.Received(1).StopAsync();
        }

        using Socket released = CreateUdpListenerSocket(IPAddress.Any, port);
    }

    [Test]
    [NonParallelizable]
    public async Task StartAsync_InitializesDiscoveryAppsOnlyAfterFallbackBindSucceeds()
    {
        int port = GetAvailableUdpPort();

        NetworkConfig networkConfig = new() { DiscoveryPort = port };
        NetworkListenerState listenerState = new(IPAddress.Any, IPAddress.IPv6Any, LimboLogs.Instance);
        IDiscoveryApp discoveryApp = Substitute.For<IDiscoveryApp>();
        bool initializedOnEventLoop = false;
        discoveryApp.When(app => app.InitializeChannel(Arg.Any<IChannel>())).Do(call =>
            initializedOnEventLoop = ((IChannel)call[0]).EventLoop.InEventLoop);
        discoveryApp.StartAsync().Returns(Task.CompletedTask);
        discoveryApp.StopAsync().Returns(Task.CompletedTask);
        RecordingChannelFactory channelFactory = new();
        CompositeDiscoveryApp app = new(
            networkConfig,
            new DiscoveryConfig(),
            LimboLogs.Instance,
            listenerState,
            [discoveryApp],
            channelFactory);

        try
        {
            await app.StartAsync();
            await discoveryApp.Received(1).StartAsync();

            Assert.That(channelFactory.CreatedChannels, Has.Count.EqualTo(2));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(listenerState.DiscoveryAddress, Is.EqualTo(IPAddress.Any));
                Assert.That(channelFactory.CreatedChannels[0].Open, Is.False);
                Assert.That(initializedOnEventLoop, Is.True);
                discoveryApp.Received(1).InitializeChannel(channelFactory.CreatedChannels[1]);
            }
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [TestCase("0.0.0.0", "2001:db8::1", "192.0.2.1", 30304)]
    [TestCase("2001:db8::5", "192.0.2.1", "2001:db8::1", 30305)]
    [TestCase("::", "2001:db8::1", "2001:db8::1", 30305)]
    public void TryCreateReachableDiscoveryNode_SelectsReachableFamily(
        string localIp,
        string preferredIp,
        string expectedIp,
        int expectedDiscoveryPort)
    {
        NodeRecord record = CreateDualStackRecord();

        bool result = CompositeDiscoveryApp.TryCreateReachableDiscoveryNode(
            record,
            IPAddress.Parse(localIp),
            new IPEndPoint(IPAddress.Parse(preferredIp), 40404),
            out Node? node);

        Assert.That(result, Is.True);
        Assert.That(node, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node!.Host, Is.EqualTo(expectedIp));
            Assert.That(node.DiscoveryPort, Is.EqualTo(expectedDiscoveryPort));
        }
    }

    [Test]
    public void TryCreateReachableDiscoveryNode_RejectsFamilyOutsideListener()
    {
        NodeRecord record = new();
        record.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
        record.SetEntry(new Ip6Entry(IPAddress.Parse("2001:db8::1")));
        record.SetEntry(new Udp6Entry(30305));

        bool result = CompositeDiscoveryApp.TryCreateReachableDiscoveryNode(
            record,
            IPAddress.Any,
            preferredEndpoint: null,
            out Node? node);

        Assert.That(result, Is.False);
        Assert.That(node, Is.Null);
    }

    [TestCase("0.0.0.0", true, false)]
    [TestCase("127.0.0.1", true, false)]
    [TestCase("::1", false, true)]
    [TestCase("::", true, true)]
    [NonParallelizable]
    public async Task Listener_HonorsExplicitAddress(string configuredIp, bool acceptsIpv4, bool acceptsIpv6)
    {
        IPAddress address = IPAddress.Parse(configuredIp);
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host.");
        }

        NetworkListenerState listenerState = CreateListenerState(configuredIp, address);
        DiscoveryConnectionsPool pool = CreatePool(listenerState);
        IEventLoopGroup eventLoopGroup = new MultithreadEventLoopGroup(1);
        int expectedDatagrams = (acceptsIpv4 ? 1 : 0) + (acceptsIpv6 ? 1 : 0);
        int receivedDatagrams = 0;
        TaskCompletionSource received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            IChannel channel = await pool.BindAsync(
                () => CreateBootstrap(eventLoopGroup, () =>
                {
                    if (Interlocked.Increment(ref receivedDatagrams) == expectedDatagrams)
                    {
                        received.TrySetResult();
                    }
                }),
                bindAddress => CreateChannel(bindAddress),
                0);
            int port = ((IPEndPoint)channel.LocalAddress).Port;

            if (acceptsIpv4)
            {
                await SendAsync(AddressFamily.InterNetwork, IPAddress.Loopback, port);
            }
            else
            {
                using Socket ipv4Probe = CreateUdpListenerSocket(IPAddress.Any, port);
            }

            if (acceptsIpv6)
            {
                await SendAsync(AddressFamily.InterNetworkV6, IPAddress.IPv6Loopback, port);
            }
            else if (Socket.OSSupportsIPv6)
            {
                using Socket ipv6Probe = CreateUdpListenerSocket(IPAddress.IPv6Any, port);
            }

            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(listenerState.DiscoveryAddress, Is.EqualTo(address));
                Assert.That(receivedDatagrams, Is.EqualTo(expectedDatagrams));
            }
        }
        finally
        {
            await pool.StopAsync();
            await eventLoopGroup.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
    }

    [Test]
    [NonParallelizable]
    public async Task WidenedBind_FallsBackToIpv4AndReceivesDatagram([Values] bool portInUse)
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host.");
        }

        using Socket? ipv6Blocker = portInUse ? CreateUdpListenerSocket(IPAddress.IPv6Any, 0) : null;
        int port = ipv6Blocker is not null ? ((IPEndPoint)ipv6Blocker.LocalEndPoint!).Port : GetAvailableUdpPort();
        NetworkListenerState listenerState = new(IPAddress.Any, IPAddress.IPv6Any, LimboLogs.Instance);
        InterfaceLogger underlyingLogger = Substitute.For<InterfaceLogger>();
        underlyingLogger.IsWarn.Returns(true);
        DiscoveryConnectionsPool pool = CreatePool(listenerState, new ILogger(underlyingLogger));
        IEventLoopGroup eventLoopGroup = new MultithreadEventLoopGroup(1);
        TaskCompletionSource received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<IChannel> createdChannels = [];
        try
        {
            await pool.BindAsync(
                () => CreateBootstrap(eventLoopGroup, () => received.TrySetResult()),
                address => CreateChannel(portInUse ? address : IPAddress.Any, createdChannels),
                port);

            await SendAsync(AddressFamily.InterNetwork, IPAddress.Loopback, port);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            underlyingLogger.Received(1).Warn(Arg.Is<string>(message =>
                message.StartsWith("Failed to bind discovery UDP channel") && message.Contains(typeof(SocketException).FullName!)));
            Assert.That(createdChannels, Has.Count.EqualTo(2));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(listenerState.DiscoveryAddress, Is.EqualTo(IPAddress.Any));
                Assert.That(createdChannels[0].Open, Is.False);
                Assert.That(createdChannels[0].CloseCompletion.IsCompletedSuccessfully, Is.True);
            }
        }
        finally
        {
            await pool.StopAsync();
            await eventLoopGroup.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
    }

    [Test]
    [NonParallelizable]
    public async Task CollisionOnFallback_FailsAndReleasesChannels()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host.");
        }

        int port;
        using (Socket ipv4Blocker = CreateUdpListenerSocket(IPAddress.Any, 0))
        {
            port = ((IPEndPoint)ipv4Blocker.LocalEndPoint!).Port;
            NetworkListenerState listenerState = new(IPAddress.Any, IPAddress.IPv6Any, LimboLogs.Instance);
            InterfaceLogger underlyingLogger = Substitute.For<InterfaceLogger>();
            underlyingLogger.IsError.Returns(true);
            DiscoveryConnectionsPool pool = CreatePool(listenerState, new ILogger(underlyingLogger));
            IEventLoopGroup eventLoopGroup = new MultithreadEventLoopGroup(1);
            List<IChannel> createdChannels = [];
            try
            {
                Assert.That(
                    async () => await pool.BindAsync(
                        () => CreateBootstrap(eventLoopGroup),
                        _ => CreateChannel(IPAddress.Any, createdChannels),
                        port),
                    Throws.TypeOf<PortInUseException>());
                Assert.That(createdChannels, Has.Count.EqualTo(2));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(listenerState.DiscoveryAddress, Is.Null);
                    AssertChannelsClosed(createdChannels);
                }
            }
            finally
            {
                await pool.StopAsync();
                await eventLoopGroup.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.FromSeconds(1));
            }

            underlyingLogger.Received(1).Error(
                Arg.Is<string>(message => message.StartsWith("Error when establishing discovery connection")),
                Arg.Any<Exception>());
            underlyingLogger.DidNotReceive().Error(
                "Error during udp channel stop process",
                Arg.Any<Exception>());
        }

        using Socket releasedIpv4 = CreateUdpListenerSocket(IPAddress.Any, port);
    }

    [TestCase(null, "0.0.0.0", "0.0.0.0", Description = "Default listener")]
    [TestCase("::", "::", "::", Description = "Explicit dual-stack listener")]
    [NonParallelizable]
    public async Task Listener_SurfacesCollision(string? configuredIp, string localIp, string blockerIp)
    {
        if ((localIp == "::" || blockerIp == "::") && !Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host.");
        }

        IPAddress blockerAddress = IPAddress.Parse(blockerIp);
        int port;
        using (Socket blocker = CreateUdpListenerSocket(blockerAddress, 0))
        {
            port = ((IPEndPoint)blocker.LocalEndPoint!).Port;
            NetworkListenerState listenerState = CreateListenerState(configuredIp, IPAddress.Parse(localIp));
            DiscoveryConnectionsPool pool = CreatePool(listenerState);
            IEventLoopGroup eventLoopGroup = new MultithreadEventLoopGroup(1);
            try
            {
                Assert.That(
                    async () => await pool.BindAsync(
                        () => CreateBootstrap(eventLoopGroup),
                        address => CreateChannel(address),
                        port),
                    Throws.TypeOf<PortInUseException>());
                Assert.That(listenerState.DiscoveryAddress, Is.Null);
            }
            finally
            {
                await pool.StopAsync();
                await eventLoopGroup.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.FromSeconds(1));
            }
        }

        using Socket released = CreateUdpListenerSocket(blockerAddress, port);
    }

    [Test]
    [NonParallelizable]
    public async Task ListenerStateSubscriberFailure_DoesNotAffectBindOrStop()
    {
        NetworkListenerState listenerState = CreateListenerState("0.0.0.0", IPAddress.Any);
        listenerState.Changed += (_, _) => throw new InvalidOperationException("subscriber failure");
        DiscoveryConnectionsPool pool = CreatePool(listenerState);
        IEventLoopGroup eventLoopGroup = new MultithreadEventLoopGroup(1);
        bool stopped = false;
        try
        {
            await pool.BindAsync(
                () => CreateBootstrap(eventLoopGroup),
                address => CreateChannel(address),
                0);
            Assert.That(listenerState.DiscoveryAddress, Is.EqualTo(IPAddress.Any));

            await pool.StopAsync();
            stopped = true;
            Assert.That(listenerState.DiscoveryAddress, Is.Null);
        }
        finally
        {
            if (!stopped)
            {
                await pool.StopAsync();
            }
            await eventLoopGroup.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
    }

    [Test]
    [NonParallelizable]
    public async Task ListenerState_ClearsWhenChannelClosesUnexpectedly()
    {
        NetworkListenerState listenerState = CreateListenerState("0.0.0.0", IPAddress.Any);
        DiscoveryConnectionsPool pool = CreatePool(listenerState);
        IEventLoopGroup eventLoopGroup = new MultithreadEventLoopGroup(1);
        try
        {
            IChannel channel = await pool.BindAsync(
                () => CreateBootstrap(eventLoopGroup),
                address => CreateChannel(address),
                0);
            TaskCompletionSource cleared = new(TaskCreationOptions.RunContinuationsAsynchronously);
            listenerState.Changed += (_, _) =>
            {
                if (listenerState.DiscoveryAddress is null) cleared.TrySetResult();
            };

            await channel.CloseAsync();
            await cleared.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(listenerState.DiscoveryAddress, Is.Null);
        }
        finally
        {
            await pool.StopAsync();
            await eventLoopGroup.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
    }

    private static NodeRecord CreateDualStackRecord()
    {
        NodeRecord record = new();
        record.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
        record.SetEntry(new IpEntry(IPAddress.Parse("192.0.2.1")));
        record.SetEntry(new TcpEntry(30303));
        record.SetEntry(new UdpEntry(30304));
        record.SetEntry(new Ip6Entry(IPAddress.Parse("2001:db8::1")));
        record.SetEntry(new Tcp6Entry(30306));
        record.SetEntry(new Udp6Entry(30305));
        return record;
    }

    private static DiscoveryConnectionsPool CreatePool(NetworkListenerState listenerState, ILogger? logger = null)
        => new(
            logger ?? LimboLogs.Instance.GetClassLogger<DiscoveryConnectionsPool>(),
            new DiscoveryConfig { UdpChannelCloseTimeout = 1_000 },
            listenerState);

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

    private static NetworkListenerState CreateListenerState(string? localIpConfig, IPAddress localIp)
    {
        NetworkConfig networkConfig = new() { LocalIp = localIpConfig };
        IIPResolver ipResolver = Substitute.For<IIPResolver>();
        ipResolver.Resolve(Arg.Any<CancellationToken>()).Returns(
            new ValueTask<IIPResolver.NethermindIp>(new IIPResolver.NethermindIp(localIp, IPAddress.Loopback)));
        return new NetworkListenerState(networkConfig, ipResolver, LimboLogs.Instance);
    }

    private static Bootstrap CreateBootstrap(IEventLoopGroup eventLoopGroup, Action? onReceive = null)
        => new Bootstrap()
            .Group(eventLoopGroup)
            .Handler(new ActionChannelInitializer<IDatagramChannel>(channel =>
            {
                if (onReceive is not null)
                {
                    channel.Pipeline.AddLast(new DatagramObserver(onReceive));
                }
            }));

    private static IChannel CreateChannel(IPAddress address, List<IChannel>? createdChannels = null)
    {
        IChannel channel = new SocketDatagramChannel(CompositeDiscoveryApp.CreateDatagramSocket(address));
        createdChannels?.Add(channel);
        return channel;
    }

    private static async Task SendAsync(AddressFamily addressFamily, IPAddress address, int port)
    {
        using Socket socket = new(addressFamily, SocketType.Dgram, ProtocolType.Udp);
        await socket.SendToAsync(new byte[] { 1 }, SocketFlags.None, new IPEndPoint(address, port));
    }

    private static Socket CreateUdpListenerSocket(IPAddress address, int port)
    {
        Socket socket = new(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.ExclusiveAddressUse = true;
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                socket.DualMode = false;
            }

            socket.Bind(new IPEndPoint(address, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static int GetAvailableUdpPort()
    {
        using Socket socket = CreateUdpListenerSocket(IPAddress.Any, 0);
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private sealed class DatagramObserver(Action onReceive) : SimpleChannelInboundHandler<DatagramPacket>
    {
        protected override void ChannelRead0(IChannelHandlerContext context, DatagramPacket message) => onReceive();
    }

    private sealed class RecordingChannelFactory : IChannelFactory
    {
        public List<IChannel> CreatedChannels { get; } = [];

        public IChannel CreateDatagramChannel() => CreateChannel(IPAddress.Any, CreatedChannels);

        public IServerChannel CreateServer() => throw new NotSupportedException();

        public IChannel CreateClient() => throw new NotSupportedException();
    }
}
