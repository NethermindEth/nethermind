// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

using System.Buffers.Binary;

using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Collections;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Stats;

using Nethermind.Core.Exceptions;

using Nethermind.Network.Test.P2P;

namespace Nethermind.Network.Test;

[NonParallelizable]
[TestFixture]
public class RlpxHostIntegrationTests
{
    [Test]
    public async Task Three_honest_peers_behind_one_nat_complete_auth()
    {
        using CryptoRandom random = new();
        HandshakeService recipient = RlpxHostIntegrationTests.CreateHandshakeService(TestItem.PrivateKeyA, random);
        int port = GetAvailablePort();
        await using IContainer container = CreateListenerContainer("127.0.0.1", IPAddress.Loopback, port,
            filterEnabled: false, handshakeService: recipient);
        RlpxHost host = container.Resolve<RlpxHost>();
        List<TcpClient> clients = [];
        int authenticated = 0;
        host.SessionCreated += (_, args) => args.Session.HandshakeComplete += (_, _) => Interlocked.Increment(ref authenticated);
        try
        {
            await host.Init();
            for (int i = 0; i < 3; i++) clients.Add(await ConnectFromAsync(IPAddress.Loopback, port));
            Assert.That(() => host.PendingInboundConnections, Is.EqualTo(3).After(2000, 10));
            PrivateKey[] keys = [TestItem.PrivateKeyB, TestItem.PrivateKeyC, TestItem.PrivateKeyD];
            for (int i = 0; i < clients.Count; i++)
                await RlpxHostIntegrationTests.AuthenticateAsync(clients[i],
                    RlpxHostIntegrationTests.CreateHandshakeService(keys[i], random));
            Assert.That(() => Volatile.Read(ref authenticated), Is.EqualTo(3).After(2000, 10));
            Assert.That(host.PendingInboundConnections, Is.EqualTo(3));
        }
        finally
        {
            foreach (TcpClient client in clients) client.Dispose();
            await host.Shutdown();
        }
        Assert.That(host.PendingInboundConnections, Is.Zero);
    }

    private static async Task<TcpClient> ConnectFromAsync(IPAddress source, int port, CancellationToken cancellationToken = default)
    {
        TcpClient client = new(new IPEndPoint(source, 0));
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    [Test]
    public async Task Failed_auth_or_handshake_timeout_returns_pending_capacity([Values] bool invalidAuth)
    {
        int port = GetAvailablePort();
        IHandshakeService handshake = Substitute.For<IHandshakeService>();
        handshake.Ack(Arg.Any<EncryptionHandshake>(), Arg.Any<Packet>())
            .Returns(_ => throw new InvalidOperationException("invalid AUTH"));
        await using IContainer container = CreateListenerContainer("127.0.0.1", IPAddress.Loopback, port,
            maxPendingInboundConnections: 1, filterEnabled: false, handshakeService: handshake);
        RlpxHost host = container.Resolve<RlpxHost>();
        using TcpClient client = new();
        try
        {
            await host.Init();
            await client.ConnectAsync(IPAddress.Loopback, port);
            Assert.That(() => host.PendingInboundConnections, Is.EqualTo(1).After(2000, 10));
            if (invalidAuth) await client.GetStream().WriteAsync(new byte[] { 0, 1, 0 });
            Assert.That(await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)), Is.Zero);
            Assert.That(() => host.PendingInboundConnections, Is.Zero.After(2000, 10));
            handshake.Received(invalidAuth ? 1 : 0).Ack(Arg.Any<EncryptionHandshake>(), Arg.Any<Packet>());
        }
        finally
        {
            await host.Shutdown();
        }
        Assert.That(host.PendingInboundConnections, Is.Zero);
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
        await using IContainer container = CreateFilterContainer(filterEnabled, subnetBucketing, externalIp);
        RlpxHost host = container.Resolve<RlpxHost>();
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(host.ShouldContact(IPAddress.Parse(addr1)), Is.True, "first IP should be accepted");
                Assert.That(host.ShouldContact(IPAddress.Parse(addr2)), Is.EqualTo(secondExpected));
            }
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
            receivedSession.Direction.Returns(ConnectionDirection.Out);
            receivedSession.RemoteHost.Returns(receivedIp.ToString());
            receivedSession.Node.Returns(new Node(TestItem.PublicKeyA, receivedIp.ToString(), 30303));

            host.TrackSessionActivity(receivedSession);
            receivedSession.MsgReceived += Raise.EventWith(receivedSession, new PeerEventArgs(receivedSession.Node, "eth", 1, 32));

            Assert.That(host.ShouldContact(receivedIp), Is.False, "received traffic should keep the active session filtered");

            IPAddress deliveredIp = IPAddress.Parse("198.51.100.1");
            ISession deliveredSession = Substitute.For<ISession>();
            deliveredSession.Direction.Returns(ConnectionDirection.Out);
            deliveredSession.RemoteHost.Returns(deliveredIp.ToString());
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

    [Test]
    public async Task Live_session_source_survives_filter_rotation([Values(ConnectionDirection.In, ConnectionDirection.Out)] ConnectionDirection direction)
    {
        await using IContainer container = CreateFilterContainer(filterEnabled: true, subnetBucketing: true);
        RlpxHost host = container.Resolve<RlpxHost>();
        IPAddress source = IPAddress.Parse("2001:db8:ffff::1");
        ISession session = Substitute.For<ISession>();
        session.Direction.Returns(direction);
        session.State.Returns(SessionState.HandshakeComplete);
        session.RemoteHost.Returns(source.ToString());
        session.Node.Returns(new Node(TestItem.PublicKeyA, source.ToString(), 30303));
        try
        {
            Assert.That(host.ShouldContact(source), Is.True);
            RlpxHost.SessionActivitySubscription subscription = host.TrackSessionActivity(session);
            for (int i = 0; i < 1000; i++)
                Assert.That(host.ShouldContact(IPAddress.Parse($"2001:db8:{i:x}::1")), Is.True);
            Assert.That(host.ShouldContact(IPAddress.Parse("2001:db8:ffff::2")), Is.False);
            subscription.Detach();
            Assert.That(host.ShouldContact(source), Is.True, "the live-session guard must release after the underlying cache entry was evicted");
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
    public async Task Shutdown_ReturnsOnlyAfterEventLoopsTerminate()
    {
        int port = GetAvailablePort();
        await using IContainer container = CreateListenerContainer(null, IPAddress.Loopback, port, shutdownCloseTimeoutMs: 6_000);
        RlpxHost host = container.Resolve<RlpxHost>();
        TaskCompletionSource sessionCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SessionCreated += (_, _) => sessionCreated.TrySetResult();
        await host.Init();
        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        await sessionCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(host.ActiveChannelCount, Is.EqualTo(1));

        Task<int> received = client.ReceiveAsync(new byte[1], SocketFlags.None);
        Task shutdown = host.Shutdown();
        int got = await received.WaitAsync(TimeSpan.FromSeconds(1));
        bool shutdownDoneAtEof = shutdown.IsCompleted;
        await shutdown.WaitAsync(TimeSpan.FromSeconds(30));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(got, Is.Zero);
            Assert.That(shutdownDoneAtEof, Is.False);
            Assert.That(host.EventLoopsTerminated, Is.True);
            Assert.That(host.ActiveChannelCount, Is.Zero);
        }
    }

    [Test]
    public async Task Shutdown_ClosesOutboundChannelsBeforeEventLoopsTerminate()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int listenerPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using IContainer container = CreateListenerContainer(null, IPAddress.Loopback, GetAvailablePort(), shutdownCloseTimeoutMs: 6_000);
        RlpxHost host = container.Resolve<RlpxHost>();
        TaskCompletionSource sessionCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SessionCreated += (_, _) => sessionCreated.TrySetResult();
        await host.Init();
        Assert.That(await host.ConnectAsync(new Node(TestItem.PublicKeyA, "127.0.0.1", listenerPort)), Is.True);
        using Socket accepted = await listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await sessionCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(host.ActiveChannelCount, Is.EqualTo(1));

        using CancellationTokenSource eofTimeout = new(TimeSpan.FromSeconds(1));
        Task closed = ReceiveUntilClosedAsync(accepted, eofTimeout.Token);
        Task shutdown = host.Shutdown();
        await closed;
        bool shutdownDoneAtEof = shutdown.IsCompleted;
        await shutdown.WaitAsync(TimeSpan.FromSeconds(30));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(shutdownDoneAtEof, Is.False);
            Assert.That(host.EventLoopsTerminated, Is.True);
            Assert.That(host.ActiveChannelCount, Is.Zero);
        }
    }

    [Test]
    public async Task Shutdown_ClosesInboundChannelRegisteredAfterChannelsAreClosed()
    {
        int port = GetAvailablePort();
        await using IContainer container = CreateListenerContainer(null, IPAddress.Loopback, port, shutdownCloseTimeoutMs: 6_000);
        RlpxHost host = container.Resolve<RlpxHost>();
        int sessionCount = 0;
        host.SessionCreated += (_, _) => Interlocked.Increment(ref sessionCount);
        await host.Init();
        TaskCompletionSource accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BootstrapChannel!.Pipeline.AddFirst(new AcceptSignalHandler(accepted));
        using ManualResetEventSlim gate = new();
        Task shutdown = Task.CompletedTask;
        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            HoldWorkerLoops(host, gate);
            await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            shutdown = host.Shutdown();
            await host.ActiveChannelsClosing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            gate.Set();
        }

        int got = await client.ReceiveAsync(new byte[1], SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(2));
        await shutdown.WaitAsync(TimeSpan.FromSeconds(30));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(got, Is.Zero);
            Assert.That(sessionCount, Is.Zero);
            Assert.That(host.ActiveChannelCount, Is.Zero);
        }
    }

    [Test]
    public async Task Shutdown_ClosesOutboundChannelRegisteredAfterChannelsAreClosed()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int listenerPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using IContainer container = CreateListenerContainer(null, IPAddress.Loopback, GetAvailablePort(), shutdownCloseTimeoutMs: 6_000);
        RlpxHost host = container.Resolve<RlpxHost>();
        int sessionCount = 0;
        host.SessionCreated += (_, _) => Interlocked.Increment(ref sessionCount);
        await host.Init();
        using ManualResetEventSlim gate = new();
        Task<bool> dial = Task.FromResult(false);
        Task shutdown = Task.CompletedTask;
        try
        {
            HoldWorkerLoops(host, gate);
            dial = host.ConnectAsync(new Node(TestItem.PublicKeyA, "127.0.0.1", listenerPort));
            shutdown = host.Shutdown();
            await host.ActiveChannelsClosing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            gate.Set();
        }

        bool connected = await dial.WaitAsync(TimeSpan.FromSeconds(5));
        await shutdown.WaitAsync(TimeSpan.FromSeconds(30));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connected, Is.False);
            Assert.That(listener.Pending(), Is.False);
            Assert.That(sessionCount, Is.Zero);
            Assert.That(host.ActiveChannelCount, Is.Zero);
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

#pragma warning disable NUnit2045 // The assertions sequence an asynchronous state transition or wait prerequisite with dependent state checks; preserve fail-fast ordering.
            Assert.That(listenerState.RlpxAddress, Is.EqualTo(acceptsIpv6 ? IPAddress.IPv6Any : IPAddress.Any));
#pragma warning restore NUnit2045
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

#pragma warning disable NUnit2045 // The assertions sequence an asynchronous state transition or wait prerequisite with dependent state checks; preserve fail-fast ordering.
            Assert.That(listenerState.RlpxAddress, Is.EqualTo(IPAddress.Any));
#pragma warning restore NUnit2045
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
    public async Task Listener_SurfacesUnassignedAddress()
    {
        // RFC 5737 documentation address, never assigned to a local interface.
        const string unassignedIp = "192.0.2.1";
        await using IContainer container = CreateListenerContainer(unassignedIp, IPAddress.Parse(unassignedIp), GetAvailablePort());
        RlpxHost host = container.Resolve<RlpxHost>();
        NetworkListenerState listenerState = container.Resolve<NetworkListenerState>();
        try
        {
            Assert.That(async () => await host.Init(),
                Throws.TypeOf<ListenerBindException>().With.InnerException.TypeOf<SocketException>());
            Assert.That(listenerState.RlpxAddress, Is.Null);
        }
        finally
        {
            await host.Shutdown();
        }
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

    [Test]
    public async Task ConnectAsync_FallsBackToVerifiedIpv6EndpointWithoutPublishingFailedAttempt()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host.");
        }

        using TcpListener ipv6Listener = new(IPAddress.IPv6Loopback, 0);
        ipv6Listener.Start();
        int listeningPort = ((IPEndPoint)ipv6Listener.LocalEndpoint).Port;
        using Socket refused = ReserveRefusedPort(IPAddress.Loopback);
        int refusedPort = ((IPEndPoint)refused.LocalEndPoint!).Port;
        await using IContainer container = CreateListenerContainer("127.0.0.1", IPAddress.Loopback, GetAvailablePort());
        RlpxHost host = container.Resolve<RlpxHost>();
        TaskCompletionSource<ISession> sessionCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int sessionCount = 0;
        host.SessionCreated += (_, args) =>
        {
            Interlocked.Increment(ref sessionCount);
            sessionCreated.TrySetResult(args.Session);
        };
        Node node = CreateDualStackNode(refusedPort, listeningPort);
        IPEndPoint originalAddress = node.Address;
        int originalDiscoveryPort = node.DiscoveryPort;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RlpxHost.TryCreateAlternateDialNode(node, allowNonRoutable: PeerAddressPermissions.All, out Node? alternate), Is.True);
            Assert.That(alternate!.Address, Is.EqualTo(new IPEndPoint(IPAddress.IPv6Loopback, listeningPort)));
        }

        try
        {
            await host.Init();
            Task<Socket> acceptTask = ipv6Listener.AcceptSocketAsync();

            Assert.That(await host.ConnectAsync(node), Is.True);

            using Socket accepted = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
            ISession session = await sessionCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(sessionCount, Is.EqualTo(1), "a failed TCP attempt must not become a published session");
                Assert.That(session.RemoteNodeId, Is.EqualTo(node.Id));
                Assert.That(session.RemoteHost, Is.EqualTo(IPAddress.IPv6Loopback.ToString()));
                Assert.That(session.RemotePort, Is.EqualTo(listeningPort));
                Assert.That(session.Node.Address, Is.EqualTo(new IPEndPoint(IPAddress.IPv6Loopback, listeningPort)));
                Assert.That(node.Address, Is.EqualTo(originalAddress), "dial fallback must not mutate the shared node endpoint");
                Assert.That(node.DiscoveryPort, Is.EqualTo(originalDiscoveryPort), "dial fallback must not mutate discovery state");
                Assert.That(host.ShouldContact(IPAddress.IPv6Loopback), Is.False,
                    "a connected fallback must be recorded in the peer filter");
            }
        }
        finally
        {
            await host.Shutdown();
            ipv6Listener.Stop();
        }
    }

    [Test]
    public async Task ConnectAsync_DoesNotDialFilteredAlternate()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host.");
        }

        using TcpListener ipv6Listener = new(IPAddress.IPv6Loopback, 0);
        ipv6Listener.Start();
        int listeningPort = ((IPEndPoint)ipv6Listener.LocalEndpoint).Port;
        using Socket refused = ReserveRefusedPort(IPAddress.Loopback);
        int refusedPort = ((IPEndPoint)refused.LocalEndPoint!).Port;
        await using IContainer container = CreateListenerContainer("127.0.0.1", IPAddress.Loopback, GetAvailablePort());
        RlpxHost host = container.Resolve<RlpxHost>();

        try
        {
            await host.Init();
#pragma warning disable NUnit2045 // The assertions sequence an asynchronous state transition or wait prerequisite with dependent state checks; preserve fail-fast ordering.
            Assert.That(host.ShouldContact(IPAddress.IPv6Loopback), Is.True);
#pragma warning restore NUnit2045

            Assert.That(await host.ConnectAsync(CreateDualStackNode(refusedPort, listeningPort)), Is.False);
            Assert.That(ipv6Listener.Pending(), Is.False, "a filtered fallback endpoint must not be dialed");
        }
        finally
        {
            await host.Shutdown();
            ipv6Listener.Stop();
        }
    }

    [Test]
    public async Task ConnectAsync_FailedAlternateDoesNotConsumeInboundFilterCapacity()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host.");
        }

        using Socket refusedIpv4 = ReserveRefusedPort(IPAddress.Loopback);
        using Socket refusedIpv6 = ReserveRefusedPort(IPAddress.IPv6Loopback);
        await using IContainer container = CreateListenerContainer("127.0.0.1", IPAddress.Loopback, GetAvailablePort());
        RlpxHost host = container.Resolve<RlpxHost>();

        try
        {
            await host.Init();

#pragma warning disable NUnit2045 // The assertions sequence an asynchronous state transition or wait prerequisite with dependent state checks; preserve fail-fast ordering.
            Assert.That(await host.ConnectAsync(CreateDualStackNode(((IPEndPoint)refusedIpv4.LocalEndPoint!).Port, ((IPEndPoint)refusedIpv6.LocalEndPoint!).Port)), Is.False);
#pragma warning restore NUnit2045
            Assert.That(host.ShouldContact(IPAddress.IPv6Loopback), Is.True,
                "a failed speculative fallback must not suppress inbound peers from the same filter bucket");
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task ConnectAsync_ReleasesChannelWhenConnectFailsAsynchronously()
    {
        AsyncRefusingClientChannelFactory channelFactory = new();
        await using IContainer container = CreateListenerContainer(
            null,
            IPAddress.Loopback,
            GetAvailablePort(),
            channelFactory: channelFactory);
        RlpxHost host = container.Resolve<RlpxHost>();

        try
        {
            await host.Init();

#pragma warning disable NUnit2045 // The assertions sequence an asynchronous state transition or wait prerequisite with dependent state checks; preserve fail-fast ordering.
            Assert.That(await host.ConnectAsync(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303)), Is.False);
#pragma warning restore NUnit2045
            using (Assert.EnterMultipleScope())
            {
                Assert.That(channelFactory.CreatedClientChannels, Is.EqualTo(1));
                Assert.That(() => channelFactory.ClientChannel!.Open, Is.False.After(5000, 10));
                Assert.That(() => host.ActiveChannelCount, Is.Zero.After(5000, 10));
            }
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task ConnectAsync_PrimarySuccessDoesNotDialAlternate()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host.");
        }

        using TcpListener ipv4Listener = new(IPAddress.Loopback, 0);
        using TcpListener ipv6Listener = new(IPAddress.IPv6Loopback, 0);
        ipv4Listener.Start();
        ipv6Listener.Start();
        int ipv4Port = ((IPEndPoint)ipv4Listener.LocalEndpoint).Port;
        int ipv6Port = ((IPEndPoint)ipv6Listener.LocalEndpoint).Port;
        await using IContainer container = CreateListenerContainer("127.0.0.1", IPAddress.Loopback, GetAvailablePort());
        RlpxHost host = container.Resolve<RlpxHost>();
        int sessionCount = 0;
        host.SessionCreated += (_, _) => Interlocked.Increment(ref sessionCount);

        try
        {
            await host.Init();
            Assert.That(await host.ConnectAsync(CreateDualStackNode(ipv4Port, ipv6Port)), Is.True);
            using Socket accepted = await ipv4Listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(sessionCount, Is.EqualTo(1));
                Assert.That(ipv6Listener.Pending(), Is.False);
            }
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task ConnectAsync_CancellationDoesNotStartFallbackOrPublishSession()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host.");
        }

        PendingClientChannelFactory channelFactory = new();
        await using IContainer container = CreateListenerContainer(
            "127.0.0.1",
            IPAddress.Loopback,
            GetAvailablePort(),
            channelFactory: channelFactory);
        RlpxHost host = container.Resolve<RlpxHost>();
        int sessionCount = 0;
        host.SessionCreated += (_, _) => Interlocked.Increment(ref sessionCount);
        using CancellationTokenSource cancellation = new();

        try
        {
            await host.Init();
            Task<bool> connectTask = host.ConnectAsync(CreateDualStackNode(30303, 30304), cancellation.Token);
            await channelFactory.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            Assert.That(
                async () => await connectTask,
                Throws.InstanceOf<OperationCanceledException>());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(sessionCount, Is.Zero);
                Assert.That(channelFactory.CreatedClientChannels, Is.EqualTo(1), "cancellation must not start the alternate family");
            }

            await channelFactory.ClientChannel!.CloseAsync();
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task ConnectAsync_SkipsRejectedPrimaryAndDialsVerifiedAlternate()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host.");
        }

        using TcpListener listener = new(IPAddress.IPv6Loopback, 0);
        listener.Start();
        RedirectingClientChannelFactory channelFactory = new((IPEndPoint)listener.LocalEndpoint);
        await using IContainer container = CreateListenerContainer(
            "127.0.0.1",
            IPAddress.Loopback,
            GetAvailablePort(),
            channelFactory: channelFactory);
        RlpxHost host = container.Resolve<RlpxHost>();
        Node node = CreateNode(CreateDualStackRecord(
            IPAddress.Any,
            30303,
            IPAddress.Parse("2606:4700:4700::1111"),
            30304,
            sequence: 0), verified: true);

        try
        {
            await host.Init();
            Task<Socket> acceptTask = listener.AcceptSocketAsync();
            Assert.That(host.IsEndpointRejected(node), Is.False);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(host.IsEndpointRejected(node), Is.False, "eligibility must not consume alternate contact pacing");

                Assert.That(await host.ConnectAsync(node), Is.True);
            }

            using Socket accepted = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(node.Address.Address, Is.EqualTo(IPAddress.Any));
                Assert.That(channelFactory.CreatedClientChannels, Is.EqualTo(1));
                Assert.That(channelFactory.RequestedRemoteEndpoint.Address,
                    Is.EqualTo(IPAddress.Parse("2606:4700:4700::1111")));
                Assert.That(channelFactory.RequestedRemoteEndpoint.Port, Is.EqualTo(30304));
            }
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [TestCase("127.0.0.1", "::1", 2, TestName = "ConnectAsync attempts each family at most once")]
    [TestCase("8.8.8.8", "fc00::1", 1, TestName = "ConnectAsync does not fall back from a public primary to a private alternate")]
    public async Task ConnectAsync_AttemptsEachFamilyAtMostOnce(string primary, string alternate, int expectedDials)
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host.");
        }

        FailingClientChannelFactory channelFactory = new();
        await using IContainer container = CreateListenerContainer(
            "127.0.0.1",
            IPAddress.Loopback,
            GetAvailablePort(),
            channelFactory: channelFactory);
        RlpxHost host = container.Resolve<RlpxHost>();

        try
        {
            await host.Init();

            bool connected = await host.ConnectAsync(CreateDualStackNode(
                IPAddress.Parse(primary), 30303,
                IPAddress.Parse(alternate), 30304,
                verified: true));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(connected, Is.False);
                Assert.That(channelFactory.CreatedClientChannels, Is.EqualTo(expectedDials));
            }
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [TestCase("8.8.8.8", false, true)]
    [TestCase("10.0.0.1", false, false)]
    [TestCase("10.0.0.1", true, true)]
    [TestCase("100.64.0.1", false, false)]
    [TestCase("fc00::1", false, false)]
    [TestCase("fc00::1", true, true)]
    [TestCase("0.0.0.0", true, false)]
    [TestCase("224.0.0.1", true, false)]
    [TestCase("192.0.2.1", true, false)]
    [TestCase("2001:db8::1", true, false)]
    public void Dial_address_policy_rejects_invalid_and_public_to_private_transitions(
        string address,
        bool allowNonRoutable,
        bool expected) =>
        Assert.That(RlpxHost.IsDialAddressAcceptable(IPAddress.Parse(address), allowNonRoutable ? PeerAddressPermissions.All : PeerAddressPermissions.None), Is.EqualTo(expected));

    [Test]
    public void Alternate_dial_requires_current_verified_record()
    {
        Node unverified = CreateDualStackNode(
            IPAddress.Parse("8.8.8.8"), 30303,
            IPAddress.Parse("2606:4700:4700::1111"), 30304,
            verified: false);
        unverified.IsStatic = true;
        unverified.IsTrusted = true;
        unverified.IsBootnode = true;
        Assert.That(RlpxHost.TryCreateAlternateDialNode(unverified, allowNonRoutable: PeerAddressPermissions.None, out _), Is.False,
            "peer role flags must not authenticate an ENR");

        Node stale = CreateDualStackNode(
            IPAddress.Parse("8.8.8.8"), 30303,
            IPAddress.Parse("2606:4700:4700::1111"), 30304,
            verified: true,
            sequence: 1);
        stale.ObserveEnrSequence(2);
        Assert.That(RlpxHost.TryCreateAlternateDialNode(stale, allowNonRoutable: PeerAddressPermissions.None, out _), Is.False);
    }

    [TestCase("10.0.0.1", "::1", false)]
    [TestCase("fc00::1", "127.0.0.1", false)]
    [TestCase("127.0.0.1", "::1", true)]
    [TestCase("::1", "127.0.0.1", true)]
    [TestCase("10.0.0.1", "fc00::1", true)]
    [TestCase("fc00::1", "10.0.0.1", true)]
    public void Alternate_dial_only_allows_loopback_from_loopback(string primaryAddress, string alternateAddress, bool expected)
    {
        IPAddress primary = IPAddress.Parse(primaryAddress);
        IPAddress alternate = IPAddress.Parse(alternateAddress);
        bool ipv4Primary = primary.AddressFamily == AddressFamily.InterNetwork;
        NodeRecord record = CreateDualStackRecord(
            ipv4Primary ? primary : alternate, 30303,
            ipv4Primary ? alternate : primary, 30304,
            sequence: 0);
#pragma warning disable NUnit2045 // The call establishes an output or mutable state needed by later assertions; keep the success prerequisite fail-fast.
        Assert.That(Node.TryFromEnr(record, primary.AddressFamily, out Node? node), Is.True);
#pragma warning restore NUnit2045
        Assert.That(node!.SetVerifiedEnr(record), Is.True);

        Assert.That(RlpxHost.TryCreateAlternateDialNode(node, allowNonRoutable: PeerAddressPermissions.All, out _), Is.EqualTo(expected));
    }

    [Test]
    public void Alternate_dial_obeys_withdrawal_family_validation_and_routability()
    {
        Node publicToPrivate = CreateDualStackNode(
            IPAddress.Parse("8.8.8.8"), 30303,
            IPAddress.Parse("fc00::1"), 30304,
            verified: true);
        Assert.That(RlpxHost.TryCreateAlternateDialNode(publicToPrivate, allowNonRoutable: PeerAddressPermissions.None, out _), Is.False);

        Node privateToPrivate = CreateDualStackNode(
            IPAddress.Parse("10.0.0.1"), 30303,
            IPAddress.Parse("fc00::1"), 30304,
            verified: true);
        Assert.That(RlpxHost.TryCreateAlternateDialNode(privateToPrivate, allowNonRoutable: PeerAddressPermissions.All, out _), Is.True);

        NodeRecord mappedRecord = CreateDualStackRecord(
            IPAddress.Parse("8.8.8.8"), 30303,
            IPAddress.Parse("::ffff:8.8.4.4"), 30304,
            sequence: 0);
        Node mapped = CreateNode(mappedRecord, verified: true);
        Assert.That(RlpxHost.TryCreateAlternateDialNode(mapped, allowNonRoutable: PeerAddressPermissions.None, out _), Is.False);

        NodeRecord withdrawal = CreateDualStackRecord(
            IPAddress.Parse("8.8.8.8"), 30303,
            ipv6: null, ipv6Port: 30304,
            sequence: 1);
#pragma warning disable NUnit2045 // The call establishes an output or mutable state needed by later assertions; keep the success prerequisite fail-fast.
        Assert.That(publicToPrivate.SetVerifiedEnr(withdrawal), Is.True);
        Assert.That(RlpxHost.TryCreateAlternateDialNode(publicToPrivate, allowNonRoutable: PeerAddressPermissions.None, out _), Is.False);
    }

    private static Node CreateDualStackNode(int ipv4Port, int ipv6Port)
        => CreateDualStackNode(IPAddress.Loopback, ipv4Port, IPAddress.IPv6Loopback, ipv6Port, verified: true);

    private static Node CreateDualStackNode(
        IPAddress ipv4,
        int ipv4Port,
        IPAddress ipv6,
        int ipv6Port,
        bool verified,
        ulong sequence = 0) =>
        CreateNode(CreateDualStackRecord(ipv4, ipv4Port, ipv6, ipv6Port, sequence), verified);

    private static NodeRecord CreateDualStackRecord(
        IPAddress ipv4,
        int ipv4Port,
        IPAddress? ipv6,
        int ipv6Port,
        ulong sequence)
    {
        NodeRecord record = new() { EnrSequence = sequence };
        record.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
        record.SetEntry(new IpEntry(ipv4));
        record.SetEntry(new TcpEntry(ipv4Port));
        record.SetEntry(new UdpEntry(ipv4Port));
        if (ipv6 is not null)
        {
            record.SetEntry(new Ip6Entry(ipv6));
            record.SetEntry(new Tcp6Entry(ipv6Port));
            record.SetEntry(new Udp6Entry(ipv6Port));
        }

        new NodeRecordSigner(new EthereumEcdsa(0), TestItem.PrivateKeyA).Sign(record);
        return record;
    }

    private static Node CreateNode(NodeRecord record, bool verified)
    {
        Assert.That(Node.TryFromEnr(record, out Node? node), Is.True);
        if (verified)
        {
            Assert.That(node!.SetVerifiedEnr(record), Is.True);
        }

        return node!;
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

    [Test]
    public async Task ConnectAsync_Uses_local_address_policy([Values] bool explicitlyConfigured)
    {
        FailingClientChannelFactory factory = new();
        IIPResolver resolver = Substitute.For<IIPResolver>();
        resolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
            new IIPResolver.NethermindIp(IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.8.8"))));
        NetworkConfig config = new() { LocalIp = "127.0.0.1", P2PPort = GetAvailablePort(), ProcessingThreadCount = 1, RlpxHostShutdownCloseTimeoutMs = 100 };
        NetworkListenerState listenerState = new(IPAddress.Loopback, IPAddress.Loopback, LimboLogs.Instance);
        await using IContainer container = CreateContainer(config, resolver, listenerState, channelFactory: factory);
        RlpxHost host = container.Resolve<RlpxHost>();
        try
        {
            await host.Init();
            Node node = new(TestItem.PublicKeyB, "127.0.0.1", 30303) { IsStatic = explicitlyConfigured };
            Assert.That(await host.ConnectAsync(node), Is.False);
            Assert.That(factory.CreatedClientChannels, Is.EqualTo(explicitlyConfigured ? 1 : 0));
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task ConnectAsync_Does_not_dial_own_listener()
    {
        FailingClientChannelFactory factory = new();
        int port = GetAvailablePort();
        await using IContainer container = CreateListenerContainer("127.0.0.1", IPAddress.Loopback, port, channelFactory: factory);
        RlpxHost host = container.Resolve<RlpxHost>();
        try
        {
            await host.Init();
            Assert.That(await host.ConnectAsync(new Node(TestItem.PublicKeyB, "127.0.0.1", port)), Is.False);
            Assert.That(factory.CreatedClientChannels, Is.Zero);
        }
        finally
        {
            await host.Shutdown();
        }
    }

    internal static IContainer CreateListenerContainer(
        string? localIpConfig,
        IPAddress resolvedLocalIp,
        int port,
        NetworkListenerState? listenerState = null,
        IChannelFactory? channelFactory = null,
        IPrivilegedIpProvider? privilegedIpProvider = null,
        ILogManager? logManager = null,
        int maxPendingInboundConnections = 50,
        bool filterEnabled = true,
        IHandshakeService? handshakeService = null,
        int? maxPendingInboundConnectionsPerSource = null,
        long shutdownCloseTimeoutMs = 100)
    {
        NetworkConfig networkConfig = new()
        {
            ProcessingThreadCount = 1,
            P2PPort = port,
            LocalIp = localIpConfig,
            MaxActivePeers = 50,
            MaxPendingInboundConnections = maxPendingInboundConnections,
            FilterPeersByRecentIp = filterEnabled,
            RlpxHostShutdownCloseTimeoutMs = shutdownCloseTimeoutMs
        };
        if (maxPendingInboundConnectionsPerSource is { } sourceCapacity)
            networkConfig.MaxPendingInboundConnectionsPerSource = sourceCapacity;
        IIPResolver ipResolver = Substitute.For<IIPResolver>();
        ipResolver.Resolve(Arg.Any<CancellationToken>()).Returns(
            new ValueTask<IIPResolver.NethermindIp>(new IIPResolver.NethermindIp(resolvedLocalIp, IPAddress.Loopback)));
        return CreateContainer(networkConfig, ipResolver, listenerState, channelFactory, privilegedIpProvider, logManager, handshakeService);
    }

    public static IContainer CreateContainer(
        INetworkConfig networkConfig,
        IIPResolver ipResolver,
        NetworkListenerState? listenerState = null,
        IChannelFactory? channelFactory = null,
        IPrivilegedIpProvider? privilegedIpProvider = null,
        ILogManager? logManager = null,
        IHandshakeService? handshakeService = null,
        LocalAddressSource? localAddressSource = null)
    {
        ContainerBuilder builder = new();
        builder.RegisterModule(new NetworkModule(new ConfigProvider(networkConfig)));
        builder.RegisterInstance(networkConfig);
        builder.RegisterInstance(ipResolver);
        builder.RegisterInstance(logManager ?? LimboLogs.Instance).As<ILogManager>();
        builder.RegisterInstance(Substitute.For<IMessageSerializationService>());
        builder.RegisterInstance<IHandshakeService>(handshakeService ?? new StubHandshakeService());
        builder.RegisterInstance(Substitute.For<ISessionMonitor>());
        builder.RegisterInstance(NullDisconnectsAnalyzer.Instance).As<IDisconnectsAnalyzer>();
        builder.RegisterInstance(privilegedIpProvider ?? Substitute.For<IPrivilegedIpProvider>());
        if (localAddressSource is not null) builder.RegisterInstance(localAddressSource);
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

    private static void HoldWorkerLoops(RlpxHost host, ManualResetEventSlim gate)
    {
        foreach (IEventLoop loop in host.WorkerGroup!.Items)
        {
            loop.Execute(() => gate.Wait());
        }
    }

    private static async Task ReceiveUntilClosedAsync(Socket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[256];
        while (await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, cancellationToken) > 0)
        {
        }
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

    private sealed class AcceptSignalHandler(TaskCompletionSource accepted) : ChannelHandlerAdapter
    {
        public override void ChannelRead(IChannelHandlerContext context, object message)
        {
            accepted.TrySetResult();
            context.FireChannelRead(message);
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

        public IDatagramSocket CreateDatagramSocket() => throw new NotSupportedException();
    }

    internal sealed class AcceptedChannelFactory : IChannelFactory
    {
        public ConcurrentQueue<IChannel> Accepted { get; } = new();
        public int ClosedCount
        {
            get
            {
                int count = 0;
                foreach (IChannel channel in Accepted)
                    if (channel.CloseCompletion.IsCompletedSuccessfully) count++;
                return count;
            }
        }

        public IServerChannel CreateServer()
        {
            TcpServerSocketChannel channel = new(AddressFamily.InterNetwork);
            channel.Pipeline.AddLast(new AcceptedChannelObserver(Accepted));
            return channel;
        }

        public IChannel CreateClient() => new TcpSocketChannel();
        public IDatagramSocket CreateDatagramSocket() => throw new NotSupportedException();
    }

    private sealed class AcceptedChannelObserver(ConcurrentQueue<IChannel> accepted) : ChannelHandlerAdapter
    {
        public override void ChannelRead(IChannelHandlerContext context, object message)
        {
            if (message is IChannel channel) accepted.Enqueue(channel);
            context.FireChannelRead(message);
        }
    }

    internal abstract class ClientChannelFactory : IChannelFactory
    {
        public int CreatedClientChannels { get; protected set; }

        public IServerChannel CreateServer() =>
            new TcpServerSocketChannel(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp));

        public abstract IChannel CreateClient();

        public IDatagramSocket CreateDatagramSocket() => throw new NotSupportedException();
    }

    private sealed class PendingClientChannelFactory : ClientChannelFactory
    {
        public TaskCompletionSource ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IChannel? ClientChannel { get; private set; }

        public override IChannel CreateClient()
        {
            CreatedClientChannels++;
            return ClientChannel = new PendingTcpSocketChannel(ConnectStarted);
        }
    }

    private sealed class PendingTcpSocketChannel(TaskCompletionSource connectStarted) : TcpSocketChannel(AddressFamily.InterNetwork)
    {
        protected override bool DoConnect(EndPoint remoteAddress, EndPoint localAddress)
        {
            connectStarted.TrySetResult();
            return false;
        }
    }

    private sealed class RedirectingClientChannelFactory(IPEndPoint target) : ClientChannelFactory
    {
        private readonly TaskCompletionSource<IPEndPoint> _requestedRemoteEndpoint =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IPEndPoint RequestedRemoteEndpoint => _requestedRemoteEndpoint.Task.GetAwaiter().GetResult();

        public override IChannel CreateClient()
        {
            CreatedClientChannels++;
            return new RedirectingTcpSocketChannel(target, _requestedRemoteEndpoint);
        }
    }

    private sealed class RedirectingTcpSocketChannel(
        IPEndPoint target,
        TaskCompletionSource<IPEndPoint> requestedRemoteEndpoint) : TcpSocketChannel(target.AddressFamily)
    {
        protected override bool DoConnect(EndPoint remoteAddress, EndPoint localAddress)
        {
            requestedRemoteEndpoint.TrySetResult((IPEndPoint)remoteAddress);
            return base.DoConnect(target, localAddress);
        }
    }

    internal sealed class FailingClientChannelFactory : ClientChannelFactory
    {
        public override IChannel CreateClient()
        {
            CreatedClientChannels++;
            return new FailingTcpSocketChannel();
        }
    }

    private sealed class FailingTcpSocketChannel : TcpSocketChannel
    {
        protected override bool DoConnect(EndPoint remoteAddress, EndPoint localAddress) =>
            throw new SocketException((int)SocketError.ConnectionRefused);
    }

    private sealed class AsyncRefusingClientChannelFactory : ClientChannelFactory
    {
        public IChannel? ClientChannel { get; private set; }

        public override IChannel CreateClient()
        {
            CreatedClientChannels++;
            return ClientChannel = new AsyncRefusingTcpSocketChannel();
        }
    }

    private sealed class AsyncRefusingTcpSocketChannel : TcpSocketChannel
    {
        protected override bool DoConnect(EndPoint remoteAddress, EndPoint localAddress)
        {
            SocketChannelAsyncOperation operation = new(this, false) { SocketError = SocketError.ConnectionRefused };
            AbstractSocketUnsafe socketUnsafe = (AbstractSocketUnsafe)Unsafe;
            EventLoop.Execute(() => socketUnsafe.FinishConnect(operation));
            return false;
        }
    }

    private sealed class StubHandshakeService : IHandshakeService
    {
        public Packet Auth(PublicKey remoteNodeId, EncryptionHandshake handshake, bool preEip8Format = false)
            => new([]);

        public Packet Ack(EncryptionHandshake handshake, Packet auth) => new([]);

        public void Agree(EncryptionHandshake handshake, Packet ack)
        {
        }
    }

    /// <summary>
    /// A port that refuses connections for as long as the returned socket lives: it is bound but never listens.
    /// </summary>
    /// <remarks>
    /// A port from <see cref="GetAvailablePort"/> is free again once returned, so a later pick, such as the host's own
    /// listener port, can be the same one and turn the dial that should be refused into a connection. Exclusive, so no
    /// address-reusing bind can share it either.
    /// </remarks>
    private static Socket ReserveRefusedPort(IPAddress address)
    {
        Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        socket.Bind(new IPEndPoint(address, 0));
        return socket;
    }

    public static int GetAvailablePort()
    {
        using System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }


    [Test]
    public void Full_authenticated_table_admits_unrelated_sources_without_forgetting_live_sources([Values] bool allLive)
    {
        InboundNodeFilter filter = new(4096, false, null);
        HashSet<NodeFilter.IpSubnetKey> liveSources = [];
        for (int i = 0; i < 4096; i++)
        {
            IPAddress address = IPAddress.Parse($"2001:db8:{i:x}::1");
            Assert.That(filter.TryAccept(address), Is.True);
            if (allLive || i == 0) liveSources.Add(NodeFilter.IpSubnetKey.DefaultKey(address));
        }

        for (int i = 4096; i < 8192; i++)
            Assert.That(filter.TryAccept(IPAddress.Parse($"2001:db8:{i:x}::1"), liveSources), Is.True);

        Assert.That(filter.TryAccept(IPAddress.Parse("2001:db8:1fff::2"), liveSources), Is.EqualTo(allLive));
        Assert.That(filter.TryAccept(IPAddress.Parse("2001:db8:0::2"), liveSources), Is.False);
        Assert.That(filter.TryAccept(IPAddress.Parse("2001:db8:1::2"), liveSources), Is.EqualTo(!allLive));
    }

    [Test]
    public void Inbound_admission_retains_live_restrictions_and_recovers_without_retry_refresh()
    {
        long now = 0;
        TimeProvider clock = Substitute.For<TimeProvider>();
        clock.TimestampFrequency.Returns(1000);
        clock.GetTimestamp().Returns(_ => now);
        InboundNodeFilter filter = new(2, false, null, clock);
        IPAddress live = IPAddress.Parse("2001:db8:1::1");
        IPAddress second = IPAddress.Parse("2001:db8:2::1");
        Assert.That(filter.TryAccept(live), Is.True);
        HashSet<NodeFilter.IpSubnetKey> liveSources = [NodeFilter.IpSubnetKey.DefaultKey(live)];
        Assert.That(filter.TryAccept(second), Is.True);
        for (int i = 3; i < 100; i++)
            Assert.That(filter.TryAccept(IPAddress.Parse($"2001:db8:{i:x}::1"), liveSources), Is.True);
        Assert.That(filter.TryAccept(IPAddress.Parse("2001:db8:1::2")), Is.False);

        now = 4 * 60 * 1000;
        filter.Touch(live);
        now = 6 * 60 * 1000;
        Assert.That(filter.TryAccept(second), Is.True, "expired entries return capacity");
        Assert.That(filter.TryAccept(live, liveSources), Is.False, "activity retains the original live subnet");
        now = 8 * 60 * 1000;
        Assert.That(filter.TryAccept(live, liveSources), Is.False);
        liveSources.Clear();
        now = 9 * 60 * 1000;
        Assert.That(filter.TryAccept(live), Is.True, "rejected unauthenticated retries must not extend the restriction");
    }

    internal static async Task AuthenticateAsync(TcpClient client, HandshakeService initiator, CancellationToken cancellationToken = default)
    {
        EncryptionHandshake exchange = new();
        await client.GetStream().WriteAsync(initiator.Auth(TestItem.PublicKeyA, exchange).Data, cancellationToken);
        byte[] prefix = new byte[2];
        await client.GetStream().ReadExactlyAsync(prefix, cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        byte[] ack = new byte[2 + BinaryPrimitives.ReadUInt16BigEndian(prefix)];
        prefix.CopyTo(ack, 0);
        await client.GetStream().ReadExactlyAsync(ack.AsMemory(2), cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        initiator.Agree(exchange, new Packet(ack));
    }

    private sealed class RecordingHandshakeService(HandshakeService inner) : IHandshakeService
    {
        private int _successfulAuthDecodes;
        public int SuccessfulAuthDecodes => Volatile.Read(ref _successfulAuthDecodes);

        public Packet Auth(PublicKey remoteNodeId, EncryptionHandshake handshake, bool preEip8Format = false) =>
            inner.Auth(remoteNodeId, handshake, preEip8Format);

        public Packet Ack(EncryptionHandshake handshake, Packet auth)
        {
            Packet ack = inner.Ack(handshake, auth);
            Interlocked.Increment(ref _successfulAuthDecodes);
            return ack;
        }

        public void Agree(EncryptionHandshake handshake, Packet ack) => inner.Agree(handshake, ack);
    }

    internal static HandshakeService CreateHandshakeService(PrivateKey key, CryptoRandom random)
    {
        MessageSerializationService serialization = new(
            SerializerInfo.Create(new AuthMessageSerializer()),
            SerializerInfo.Create(new AuthEip8MessageSerializer()),
            SerializerInfo.Create(new AckMessageSerializer()),
            SerializerInfo.Create(new AckEip8MessageSerializer()));
        return new HandshakeService(serialization, new EciesCipher(random), random, new EthereumEcdsa(1), key, LimboLogs.Instance);
    }

    [Test]
    public void Accepted_hello_notifies_an_alternate_session([Values] bool validIdentity)
    {
        ISession session = Substitute.For<ISession>();
        session.RemoteNodeId.Returns(TestItem.PublicKeyB);
        session.Node.Returns(new Node(TestItem.PublicKeyB, "127.0.0.1", 30303));
        MessageSerializationService serializer = new(SerializerInfo.Create(new HelloMessageSerializer()));
        using P2PProtocolHandler handler = new(session, new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303),
            Substitute.For<INodeStatsManager>(), serializer, Substitute.For<IBackgroundTaskScheduler>(), LimboLogs.Instance, new NetworkConfig());
        handler.AddSupportedCapability(new Capability(Protocol.Eth, 66));

        HandleHello(handler, serializer, validIdentity ? TestItem.PublicKeyB : TestItem.PublicKeyC);

        session.Received(validIdentity ? 1 : 0).MarkAuthenticated();
    }

    [Test]
    public async Task Pending_lease_follows_alternate_admission_subscriber([Values] bool accept)
    {
        using CryptoRandom random = new();
        HandshakeService recipient = RlpxHostIntegrationTests.CreateHandshakeService(TestItem.PrivateKeyA, random);
        HandshakeService initiator = RlpxHostIntegrationTests.CreateHandshakeService(TestItem.PrivateKeyB, random);
        int port = GetAvailablePort();
        await using IContainer container = CreateListenerContainer("127.0.0.1", IPAddress.Loopback, port,
            maxPendingInboundConnections: 1, filterEnabled: false, handshakeService: recipient);
        RlpxHost host = container.Resolve<RlpxHost>();
        TaskCompletionSource<Session> initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SessionCreated += (_, args) => args.Session.Initialized += (_, _) => initialized.TrySetResult((Session)args.Session);
        using TcpClient client = new();
        try
        {
            await host.Init();
            await client.ConnectAsync(IPAddress.Loopback, port);
            await RlpxHostIntegrationTests.AuthenticateAsync(client, initiator);
            Session session = await initialized.Task.WaitAsync(TimeSpan.FromSeconds(2));
            int authenticated = 0;
            session.Authenticated += (_, _) => authenticated++;
            Assert.That(host.PendingInboundConnections, Is.EqualTo(1), "AUTH and transport initialization retain the lease");
            MessageSerializationService serializer = new(SerializerInfo.Create(new HelloMessageSerializer()));
            using P2PProtocolHandler handler = new(session, new Enode(TestItem.PublicKeyA, IPAddress.Loopback, port),
                Substitute.For<INodeStatsManager>(), serializer, Substitute.For<IBackgroundTaskScheduler>(), LimboLogs.Instance, new NetworkConfig());
            handler.AddSupportedCapability(new Capability(Protocol.Eth, 66));
            bool admissionCalled = false;
            handler.ProtocolInitialized += (_, _) =>
            {
                admissionCalled = true;
                Assert.That(host.PendingInboundConnections, Is.EqualTo(1), "release must follow admission callbacks");
                if (!accept) session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, "alternate admission rejected Hello");
            };
            HandleHello(handler, serializer, TestItem.PublicKeyB);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(admissionCalled, Is.True);
                Assert.That(authenticated, Is.EqualTo(accept ? 1 : 0));
                Assert.That(session.IsClosing, Is.EqualTo(!accept));
                Assert.That(() => host.PendingInboundConnections, Is.Zero.After(2000, 10));
            }
        }
        finally
        {
            await host.Shutdown();
        }
    }

    private static void HandleHello(P2PProtocolHandler handler, MessageSerializationService serializer, PublicKey identity)
    {
        using HelloMessage hello = new()
        {
            NodeId = identity,
            P2PVersion = 5,
            ClientId = "honest peer",
            ListenPort = 30303,
            Capabilities = new ArrayPoolList<Capability>(1) { new(Protocol.Eth, 66) }
        };
        P2PProtocolHandlerTests.HandleHello(handler, serializer, hello);
    }

    [Test]
    public async Task Zero_still_disables_all_inbound_connections([Values] bool privileged)
    {
        IPrivilegedIpProvider privileges = Substitute.For<IPrivilegedIpProvider>();
        privileges.IsPrivileged(Arg.Any<IPAddress>()).Returns(privileged);
        int port = GetAvailablePort();
        await using IContainer container = CreateListenerContainer("127.0.0.1", IPAddress.Loopback, port,
            privilegedIpProvider: privileges, maxPendingInboundConnections: 0);
        RlpxHost host = container.Resolve<RlpxHost>();
        using TcpClient client = new();
        try
        {
            await host.Init();
            await client.ConnectAsync(IPAddress.Loopback, port);
            Assert.That(await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(2)), Is.Zero);
            Assert.That(host.PendingInboundConnections, Is.Zero);
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task Custom_configuration_rejects_negative_pending_capacity_before_starting_host([Values] bool perSource)
    {
        INetworkConfig config = Substitute.For<INetworkConfig>();
        config.MaxPendingInboundConnections.Returns(perSource ? 50 : -1);
        config.MaxPendingInboundConnectionsPerSource.Returns(perSource ? -1 : 4);
        config.RlpxHostShutdownCloseTimeoutMs.Returns(100);
        IIPResolver resolver = Substitute.For<IIPResolver>();
        resolver.Resolve(Arg.Any<CancellationToken>()).Returns(new IIPResolver.NethermindIp(IPAddress.Loopback, IPAddress.Loopback));
        using PeerAddressPolicy policy = new(resolver, new LocalAddressSource(LimboLogs.Instance, () => []));
        RlpxHost? host = null;
        try
        {
            Assert.That(() => host = new RlpxHost(policy,
                Substitute.For<IMessageSerializationService>(),
                Substitute.For<IHandshakeService>(), Substitute.For<ISessionMonitor>(), NullDisconnectsAnalyzer.Instance,
                config, resolver, Substitute.For<IPrivilegedIpProvider>(), LimboLogs.Instance, new NetworkListenerState(config, resolver, LimboLogs.Instance)),
                Throws.TypeOf<InvalidConfigurationException>());
        }
        finally
        {
            if (host is not null) await host.Shutdown();
        }
    }

    [Test]
    public async Task Privileged_address_can_start_a_handshake_when_the_ordinary_lane_is_full()
    {
        IPAddress privileged = IPAddress.Parse("192.0.2.3");
        IPrivilegedIpProvider privileges = Substitute.For<IPrivilegedIpProvider>();
        privileges.IsPrivileged(privileged).Returns(true);
        await using IContainer container = CreateListenerContainer("127.0.0.1", IPAddress.Loopback, 30303,
            privilegedIpProvider: privileges, maxPendingInboundConnections: 1, filterEnabled: false);
        RlpxHost host = container.Resolve<RlpxHost>();
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IChannel channel = Substitute.For<IChannel>();
        channel.CloseCompletion.Returns(closed.Task);
        RlpxHost.PendingInboundAdmission? Admit(IPAddress address) =>
            host.TryAdmitInbound(channel, new Session(30303, channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance), address);

        try
        {
            Assert.That(Admit(IPAddress.Parse("192.0.2.1")), Is.Not.Null);
            Assert.That(Admit(IPAddress.Parse("192.0.2.2")), Is.Null);
            Assert.That(Admit(privileged), Is.Not.Null);
        }
        finally { closed.SetResult(); }
    }

    [Test]
    public void Negative_pending_capacity_is_a_configuration_error([Values(-1, int.MinValue)] int capacity, [Values] bool perSource)
    {
        NetworkConfig config = new();
        Assert.That(() =>
        {
            if (perSource) config.MaxPendingInboundConnectionsPerSource = capacity;
            else config.MaxPendingInboundConnections = capacity;
        }, Throws.TypeOf<InvalidConfigurationException>());
    }
}
