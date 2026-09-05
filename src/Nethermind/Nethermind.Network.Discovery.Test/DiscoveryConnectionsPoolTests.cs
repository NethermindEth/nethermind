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
using Nethermind.Logging;
using Nethermind.Network.Config;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

[NonParallelizable]
public class DiscoveryConnectionsPoolTests
{
    [TestCase("0.0.0.0", true, false)]
    [TestCase("127.0.0.1", true, false)]
    [TestCase("::1", false, true)]
    [TestCase("::", true, true)]
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
            else
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
    public async Task WidenedBind_FallsBackToIpv4AndReceivesDatagram()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host.");
        }

        int port = GetAvailableUdpPort();
        NetworkListenerState listenerState = new(IPAddress.Any, IPAddress.IPv6Any, LimboLogs.Instance);
        DiscoveryConnectionsPool pool = CreatePool(listenerState);
        IEventLoopGroup eventLoopGroup = new MultithreadEventLoopGroup(1);
        TaskCompletionSource received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<IChannel> createdChannels = [];
        try
        {
            await pool.BindAsync(
                () => CreateBootstrap(eventLoopGroup, () => received.TrySetResult()),
                _ => CreateChannel(IPAddress.Any, createdChannels),
                port);

            await SendAsync(AddressFamily.InterNetwork, IPAddress.Loopback, port);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(listenerState.DiscoveryAddress, Is.EqualTo(IPAddress.Any));
            Assert.That(createdChannels[0].Open, Is.False);
            Assert.That(createdChannels[0].CloseCompletion.IsCompletedSuccessfully, Is.True);
        }
        finally
        {
            await pool.StopAsync();
            await eventLoopGroup.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
    }

    [Test]
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
                Assert.That(listenerState.DiscoveryAddress, Is.Null);
                Assert.That(createdChannels, Has.Count.EqualTo(2));
                AssertChannelsClosed(createdChannels);
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

    private static DiscoveryConnectionsPool CreatePool(NetworkListenerState listenerState, ILogger? logger = null)
        => new(
            logger ?? LimboLogs.Instance.GetClassLogger<DiscoveryConnectionsPool>(),
            new DiscoveryConfig { UdpChannelCloseTimeout = 1_000 },
            listenerState);

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

    private static NetworkListenerState CreateListenerState(string? localIpConfig = null, IPAddress? localIp = null)
    {
        NetworkConfig networkConfig = new() { LocalIp = localIpConfig };
        IIPResolver ipResolver = Substitute.For<IIPResolver>();
        ipResolver.Resolve(Arg.Any<CancellationToken>()).Returns(
            new ValueTask<IIPResolver.NethermindIp>(new IIPResolver.NethermindIp(localIp ?? IPAddress.Any, IPAddress.Loopback)));
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
}
