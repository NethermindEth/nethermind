// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
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
    public async Task StartAsync_ReleasesSocketWhenBindFails()
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
            RecordingDiscoveryApp discoveryApp = new();
            RecordingChannelFactory channelFactory = new();
            CompositeDiscoveryApp app = new(
                networkConfig,
                new DiscoveryConfig(),
                LimboLogs.Instance,
                listenerState,
                [discoveryApp],
                channelFactory);

            Assert.That(async () => await app.StartAsync(), Throws.TypeOf<PortInUseException>());

            Assert.That(channelFactory.CreatedSockets, Has.Count.EqualTo(1));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(listenerState.DiscoveryAddress, Is.Null);
                AssertSocketsClosed(channelFactory.CreatedSockets);
                Assert.That(discoveryApp.InitializedSockets, Is.Empty);
                Assert.That(discoveryApp.StopAsyncCoreCalls, Is.EqualTo(1));
            }
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
        RecordingDiscoveryApp discoveryApp = new();
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

            Assert.That(channelFactory.CreatedSockets, Has.Count.EqualTo(2));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(listenerState.DiscoveryAddress, Is.EqualTo(IPAddress.Any));
                Assert.That(channelFactory.CreatedSockets[0].SafeHandle.IsClosed, Is.True);
                Assert.That(discoveryApp.InitializedSockets, Has.Count.EqualTo(1));
                Assert.That(discoveryApp.InitializedSockets[0].LocalEndpoint, Is.EqualTo(new IPEndPoint(IPAddress.Any, port)));
            }
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Test]
    public async Task StartAsync_ForwardsDatagramsAlongProtocols()
    {
        LocalChannelFactory channelFactory = new(nameof(StartAsync_ForwardsDatagramsAlongProtocols), new NetworkConfig());
        IPEndPoint listener = new(IPAddress.Loopback, 30303);
        NetworkListenerState listenerState = new(IPAddress.Loopback, IPAddress.Loopback, LimboLogs.Instance);
        RecordingDiscoveryApp first = new(forwardAll: true);
        RecordingDiscoveryApp second = new();
        CompositeDiscoveryApp app = new(
            new NetworkConfig { DiscoveryPort = listener.Port },
            new DiscoveryConfig(),
            LimboLogs.Instance,
            listenerState,
            [first, second],
            channelFactory);
        byte[] data = [1, 2, 3];

        try
        {
            await app.StartAsync();
            using IDatagramSocket sender = channelFactory.CreateDatagramSocket();
            sender.Bind(IPEndPoint.Parse("127.0.0.2:30303"));

            await sender.SendToAsync(data, listener);

            Assert.That(await second.Received.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(data));
            Assert.That(first.InitializedSockets, Is.EqualTo(second.InitializedSockets));
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
        int expectedDatagrams = (acceptsIpv4 ? 1 : 0) + (acceptsIpv6 ? 1 : 0);
        int receivedDatagrams = 0;
        TaskCompletionSource received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            IDatagramSocket socket = pool.Bind(
                bindAddress => CreateSocket(bindAddress),
                0,
                datagram =>
                {
                    datagram.Dispose();
                    if (Interlocked.Increment(ref receivedDatagrams) == expectedDatagrams)
                    {
                        received.TrySetResult();
                    }
                });
            int port = socket.LocalEndpoint!.Port;

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
        TaskCompletionSource received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<Socket> createdSockets = [];
        try
        {
            pool.Bind(
                address => CreateSocket(portInUse ? address : IPAddress.Any, createdSockets),
                port,
                datagram =>
                {
                    datagram.Dispose();
                    received.TrySetResult();
                });

            await SendAsync(AddressFamily.InterNetwork, IPAddress.Loopback, port);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            underlyingLogger.Received(1).Warn(Arg.Is<string>(message =>
                message.StartsWith("Failed to bind discovery UDP channel") && message.Contains(typeof(SocketException).FullName!)));
            Assert.That(createdSockets, Has.Count.EqualTo(2));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(listenerState.DiscoveryAddress, Is.EqualTo(IPAddress.Any));
                Assert.That(createdSockets[0].SafeHandle.IsClosed, Is.True);
            }
        }
        finally
        {
            await pool.StopAsync();
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
            List<Socket> createdSockets = [];
            try
            {
                Assert.That(
                    () => pool.Bind(
                        _ => CreateSocket(IPAddress.Any, createdSockets),
                        port,
                        static datagram => datagram.Dispose()),
                    Throws.TypeOf<PortInUseException>());
                Assert.That(createdSockets, Has.Count.EqualTo(2));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(listenerState.DiscoveryAddress, Is.Null);
                    AssertSocketsClosed(createdSockets);
                }
            }
            finally
            {
                await pool.StopAsync();
            }

            underlyingLogger.Received(1).Error(
                Arg.Is<string>(message => message.StartsWith("Error when establishing discovery connection")),
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
            try
            {
                Assert.That(
                    () => pool.Bind(
                        address => CreateSocket(address),
                        port,
                        static datagram => datagram.Dispose()),
                    Throws.TypeOf<PortInUseException>());
                Assert.That(listenerState.DiscoveryAddress, Is.Null);
            }
            finally
            {
                await pool.StopAsync();
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
        bool stopped = false;
        try
        {
            pool.Bind(
                address => CreateSocket(address),
                0,
                static datagram => datagram.Dispose());
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
        }
    }

    [Test]
    [NonParallelizable]
    public async Task ListenerState_ClearsWhenSocketClosesUnexpectedly()
    {
        NetworkListenerState listenerState = CreateListenerState("0.0.0.0", IPAddress.Any);
        DiscoveryConnectionsPool pool = CreatePool(listenerState);
        try
        {
            IDatagramSocket socket = pool.Bind(
                address => CreateSocket(address),
                0,
                static datagram => datagram.Dispose());
            TaskCompletionSource cleared = new(TaskCreationOptions.RunContinuationsAsynchronously);
            listenerState.Changed += (_, _) =>
            {
                if (listenerState.DiscoveryAddress is null) cleared.TrySetResult();
            };

            socket.Dispose();
            await cleared.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(listenerState.DiscoveryAddress, Is.Null);
        }
        finally
        {
            await pool.StopAsync();
        }
    }

    [Test]
    public async Task Receive_SkipsDatagramsOfInvalidSize([Values(0, DiscoveryConnectionsPool.MaxPacketSize + 1)] int size)
    {
        byte[] invalid = new byte[size];
        byte[] first = [1, 2, 3];
        byte[] second = [4, 5, 6];

        List<(byte[] Data, IPEndPoint Sender)> received = await ReceiveThroughPoolAsync(
            IPEndPoint.Parse("127.0.0.2:10000"), 2, invalid, first, invalid, second);

        Assert.That(received[0].Data, Is.EqualTo(first));
        Assert.That(received[1].Data, Is.EqualTo(second));
    }

    [TestCase("::ffff:127.0.0.2", "127.0.0.2")]
    [TestCase("2001:db8::2", "2001:db8::2")]
    public async Task Receive_ReportsSenderInCanonicalForm(string senderAddress, string expectedAddress)
    {
        List<(byte[] Data, IPEndPoint Sender)> received = await ReceiveThroughPoolAsync(
            new IPEndPoint(IPAddress.Parse(senderAddress), 10000), 1, [1, 2, 3]);

        Assert.That(received[0].Sender, Is.EqualTo(new IPEndPoint(IPAddress.Parse(expectedAddress), 10000)));
    }

    [Test]
    [NonParallelizable]
    public async Task Receive_UpdatesDiscoveryBytesReceivedMetric()
    {
        byte[] data = new byte[100];
        long bytesReceivedBefore = Interlocked.Read(ref Metrics.DiscoveryBytesReceived);

        await ReceiveThroughPoolAsync(IPEndPoint.Parse("127.0.0.2:10000"), 1, data);

        Assert.That(Interlocked.Read(ref Metrics.DiscoveryBytesReceived) - bytesReceivedBefore, Is.EqualTo(data.Length));
    }

    /// <summary>
    /// Sends <paramref name="datagrams"/> from <paramref name="sender"/> to a pool listening on an in-memory socket
    /// and returns the first <paramref name="expectedCount"/> datagrams the pool delivers.
    /// </summary>
    private static async Task<List<(byte[] Data, IPEndPoint Sender)>> ReceiveThroughPoolAsync(
        IPEndPoint sender,
        int expectedCount,
        params byte[][] datagrams)
    {
        LocalChannelFactory channelFactory = new(TestContext.CurrentContext.Test.ID, new NetworkConfig());
        DiscoveryConnectionsPool pool = CreatePool(new NetworkListenerState(IPAddress.Loopback, IPAddress.Loopback, LimboLogs.Instance));
        Channel<(byte[] Data, IPEndPoint Sender)> received = Channel.CreateUnbounded<(byte[] Data, IPEndPoint Sender)>();
        try
        {
            IDatagramSocket socket = pool.Bind(_ => channelFactory.CreateDatagramSocket(), 30303, datagram =>
            {
                received.Writer.TryWrite((datagram.Buffer.ToArray(), datagram.RemoteEndPoint));
                datagram.Dispose();
            });
            using IDatagramSocket senderSocket = channelFactory.CreateDatagramSocket();
            senderSocket.Bind(sender);
            foreach (byte[] datagram in datagrams)
            {
                await senderSocket.SendToAsync(datagram, socket.LocalEndpoint!);
            }

            using CancellationTokenSource cancellationSource = new(TimeSpan.FromSeconds(5));
            List<(byte[] Data, IPEndPoint Sender)> result = [];
            while (result.Count < expectedCount)
            {
                result.Add(await received.Reader.ReadAsync(cancellationSource.Token));
            }

            return result;
        }
        finally
        {
            await pool.StopAsync();
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

    private static void AssertSocketsClosed(IReadOnlyList<Socket> sockets)
    {
        foreach (Socket socket in sockets)
        {
            Assert.That(socket.SafeHandle.IsClosed, Is.True);
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

    private static IDatagramSocket CreateSocket(IPAddress address, List<Socket>? createdSockets = null)
    {
        Socket socket = CompositeDiscoveryApp.CreateDatagramSocket(address);
        createdSockets?.Add(socket);
        return new UdpDatagramSocket(socket);
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

    private sealed class RecordingChannelFactory : IChannelFactory
    {
        public List<Socket> CreatedSockets { get; } = [];

        public IDatagramSocket CreateDatagramSocket() => CreateSocket(IPAddress.Any, CreatedSockets);

        public IServerChannel CreateServer() => throw new NotSupportedException();

        public IChannel CreateClient() => throw new NotSupportedException();
    }

    /// <summary>
    /// Records how <see cref="CompositeDiscoveryApp"/> drives a protocol, optionally forwarding every datagram.
    /// </summary>
    private sealed class RecordingDiscoveryApp(bool forwardAll = false) : KademliaDiscoveryApp(
        "test discovery",
        new NetworkConfig { ExternalIp = "127.0.0.1" },
        new FixedIpResolver(new NetworkConfig { ExternalIp = "127.0.0.1" }),
        new ProcessExitSource(CancellationToken.None),
        LimboLogs.Instance.GetClassLogger<RecordingDiscoveryApp>())
    {
        private Action<PooledUdpReceiveResult>? _forward;

        public List<IDatagramSocket> InitializedSockets { get; } = [];

        public TaskCompletionSource<byte[]> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopAsyncCoreCalls { get; private set; }

        internal override void InitializeChannel(IDatagramSocket socket, Action<PooledUdpReceiveResult> forward)
        {
            InitializedSockets.Add(socket);
            _forward = forward;
        }

        internal override void Receive(PooledUdpReceiveResult datagram)
        {
            if (forwardAll)
            {
                _forward!(datagram);
                return;
            }

            Received.TrySetResult(datagram.Buffer.ToArray());
            datagram.Dispose();
        }

        protected override Task RunDiscoveryAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override Task StopAsyncCore()
        {
            StopAsyncCoreCalls++;
            return Task.CompletedTask;
        }
    }
}
