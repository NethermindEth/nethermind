// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Sockets;
using Nethermind.Stats.Model;
using NUnit.Framework;
using static Nethermind.Network.Test.RlpxHostIntegrationTests;

namespace Nethermind.Network.Test.Rlpx.Sockets;

[NonParallelizable]
public class SocketRlpxHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private bool _diagTracerWasEnabled;

    /// <remarks>
    /// Other tests leave the tracer on, and it makes <see cref="P2P.Session.MarkDisconnected"/> throw for an inbound
    /// session that has not completed its handshake, before the disconnect is reported.
    /// </remarks>
    [SetUp]
    public void DisableDiagTracer()
    {
        _diagTracerWasEnabled = NetworkDiagTracer.IsEnabled;
        NetworkDiagTracer.IsEnabled = false;
    }

    [TearDown]
    public void RestoreDiagTracer() => NetworkDiagTracer.IsEnabled = _diagTracerWasEnabled;

    [Test]
    public async Task Is_the_rlpx_host_when_configured([Values] RlpxTransport transport)
    {
        await using IContainer container = CreateListenerContainer("127.0.0.1", IPAddress.Loopback, GetAvailablePort(), transport: transport);
        IRlpxHost host = container.Resolve<IRlpxHost>();
        try
        {
            Assert.That(host, Is.TypeOf(transport == RlpxTransport.Sockets ? typeof(SocketRlpxHost) : typeof(RlpxHost)));
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [TestCase("127.0.0.1", true, false)]
    [TestCase("::1", false, true)]
    [TestCase("::", true, true)]
    public async Task Listens_on_the_configured_address_until_shut_down(string configuredIp, bool acceptsIpv4, bool acceptsIpv6)
    {
        IPAddress address = IPAddress.Parse(configuredIp);
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host.");
        }

        int port = GetAvailablePort();
        await using IContainer container = CreateListenerContainer(configuredIp, address, port, transport: RlpxTransport.Sockets);
        IRlpxHost host = container.Resolve<IRlpxHost>();
        NetworkListenerState listenerState = container.Resolve<NetworkListenerState>();
        try
        {
            await host.Init();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(listenerState.RlpxAddress, Is.EqualTo(address));
                Assert.That(await CanConnect(AddressFamily.InterNetwork, port), Is.EqualTo(acceptsIpv4));
                Assert.That(await CanConnect(AddressFamily.InterNetworkV6, port), Is.EqualTo(acceptsIpv6));
            }

            await host.Shutdown();
            Assert.That(() => listenerState.RlpxAddress, Is.Null.After(5000, 10));
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task Falls_back_to_ipv4_when_the_widened_bind_fails()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host.");
        }

        using Socket ipv6Blocker = CreateTcpListenerSocket(IPAddress.IPv6Any, 0);
        int port = ((IPEndPoint)ipv6Blocker.LocalEndPoint!).Port;
        NetworkListenerState listenerState = new(IPAddress.Any, IPAddress.IPv6Any, LimboLogs.Instance);
        await using IContainer container = CreateListenerContainer(null, IPAddress.Any, port, listenerState, transport: RlpxTransport.Sockets);
        IRlpxHost host = container.Resolve<IRlpxHost>();
        try
        {
            await host.Init();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(listenerState.RlpxAddress, Is.EqualTo(IPAddress.Any));
                Assert.That(await CanConnect(AddressFamily.InterNetwork, port), Is.True);
            }
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task Reports_a_port_in_use()
    {
        using Socket blocker = CreateTcpListenerSocket(IPAddress.Loopback, 0);
        int port = ((IPEndPoint)blocker.LocalEndPoint!).Port;
        await using IContainer container = CreateListenerContainer("127.0.0.1", IPAddress.Loopback, port, transport: RlpxTransport.Sockets);
        IRlpxHost host = container.Resolve<IRlpxHost>();
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

    [Test]
    public async Task Reports_an_unassigned_address()
    {
        // RFC 5737 documentation address, never assigned to a local interface.
        const string unassignedIp = "192.0.2.1";
        await using IContainer container = CreateListenerContainer(unassignedIp, IPAddress.Parse(unassignedIp), GetAvailablePort(), transport: RlpxTransport.Sockets);
        IRlpxHost host = container.Resolve<IRlpxHost>();
        try
        {
            Assert.That(async () => await host.Init(), Throws.TypeOf<ListenerBindException>().With.InnerException.TypeOf<SocketException>());
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task Closes_an_inbound_connection_the_filter_rejects()
    {
        await using IContainer container = CreateFilterContainer(filterEnabled: true, subnetBucketing: false, transport: RlpxTransport.Sockets);
        SocketRlpxHost host = (SocketRlpxHost)container.Resolve<IRlpxHost>();
        int sessions = 0;
        host.SessionCreated += (_, _) => Interlocked.Increment(ref sessions);
        try
        {
            await host.Init();
            IPEndPoint listener = new(IPAddress.Loopback, host.ListenerEndPoint!.Port);
            using Socket first = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await first.ConnectAsync(listener);
            Assert.That(() => Volatile.Read(ref sessions), Is.EqualTo(1).After(5000, 10));

            using Socket second = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await second.ConnectAsync(listener);
            using CancellationTokenSource timeout = new(Timeout);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(await ReceiveAsync(second, timeout.Token), Is.Zero, "the rejected connection is closed");
                Assert.That(Volatile.Read(ref sessions), Is.EqualTo(1));
                Assert.That(host.ConnectionCount, Is.EqualTo(1));
            }
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task Closes_only_the_connection_whose_session_subscriber_throws()
    {
        await using IContainer container = CreateFilterContainer(filterEnabled: false, subnetBucketing: false, transport: RlpxTransport.Sockets);
        SocketRlpxHost host = (SocketRlpxHost)container.Resolve<IRlpxHost>();
        int sessions = 0;
        host.SessionCreated += (_, _) =>
        {
            if (Interlocked.Increment(ref sessions) == 1) throw new InvalidOperationException("Subscriber failure");
        };
        try
        {
            await host.Init();
            IPEndPoint listener = new(IPAddress.Loopback, host.ListenerEndPoint!.Port);
            using Socket first = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await first.ConnectAsync(listener);
            using CancellationTokenSource timeout = new(Timeout);
            Assert.That(await ReceiveAsync(first, timeout.Token), Is.Zero, "the failed connection is closed");

            using Socket second = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await second.ConnectAsync(listener);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(() => Volatile.Read(ref sessions), Is.EqualTo(2).After(5000, 10), "the listener still accepts");
                Assert.That(host.ConnectionCount, Is.EqualTo(1), "the failed connection is no longer tracked");
            }
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task Returns_false_for_a_refused_dial()
    {
        await using IContainer container = CreateFilterContainer(filterEnabled: false, subnetBucketing: false, transport: RlpxTransport.Sockets);
        IRlpxHost host = container.Resolve<IRlpxHost>();
        using Socket refused = ReserveRefusedPort(IPAddress.Loopback);
        try
        {
            await host.Init();
            Assert.That(await host.ConnectAsync(new Node(TestItem.PublicKeyA, (IPEndPoint)refused.LocalEndPoint!)), Is.False);
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task Throws_when_the_caller_cancels_a_dial()
    {
        await using IContainer container = CreateFilterContainer(filterEnabled: false, subnetBucketing: false, transport: RlpxTransport.Sockets);
        IRlpxHost host = container.Resolve<IRlpxHost>();
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        try
        {
            await host.Init();
            Assert.That(async () => await host.ConnectAsync(new Node(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Loopback, 30303)), canceled.Token),
                Throws.InstanceOf<OperationCanceledException>());
        }
        finally
        {
            await host.Shutdown();
        }
    }

    /// <remarks>DotNetty starts no connect timer for a zero timeout, which leaves <see cref="RlpxHost"/>'s 2-second watchdog.</remarks>
    [TestCase(0, 2000)]
    [TestCase(500, 500)]
    public async Task Gives_a_dial_the_connect_timeout_or_two_seconds_for_zero(int connectTimeoutMs, int expectedMs)
    {
        await using IContainer container = CreateFilterContainer(filterEnabled: false, subnetBucketing: false, transport: RlpxTransport.Sockets, connectTimeoutMs: connectTimeoutMs);
        SocketRlpxHost host = (SocketRlpxHost)container.Resolve<IRlpxHost>();
        try
        {
            Assert.That(host.ConnectTimeout, Is.EqualTo(TimeSpan.FromMilliseconds(expectedMs)));
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task Every_shutdown_call_waits_for_the_first()
    {
        await using IContainer container = CreateFilterContainer(filterEnabled: false, subnetBucketing: false, transport: RlpxTransport.Sockets);
        IRlpxHost host = container.Resolve<IRlpxHost>();
        await host.Init();

        Task first = host.Shutdown();
        Task second = host.Shutdown();

        Assert.That(second, Is.SameAs(first));
        await first;
    }

    [Test]
    public async Task Closes_a_listener_bound_after_shutdown_started()
    {
        await using IContainer container = CreateFilterContainer(filterEnabled: false, subnetBucketing: false, transport: RlpxTransport.Sockets);
        SocketRlpxHost host = (SocketRlpxHost)container.Resolve<IRlpxHost>();

        await host.Shutdown();
        await host.Init();

        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(host.ListenerEndPoint, Is.Null, "the listener isn't published");
            Assert.That(() => probe.Bind(new IPEndPoint(IPAddress.Any, host.LocalPort)), Throws.Nothing, "the port is free");
        }
    }

    [Test]
    public async Task Shutdown_waits_for_a_pending_dial_which_publishes_nothing()
    {
        await using IContainer container = CreateFilterContainer(filterEnabled: false, subnetBucketing: false, transport: RlpxTransport.Sockets);
        IRlpxHost host = container.Resolve<IRlpxHost>();
        int sessions = 0;
        host.SessionCreated += (_, _) => Interlocked.Increment(ref sessions);
        await host.Init();

        // A private address that answers nothing, so the dial stays pending until it is canceled.
        Task<bool> dial = host.ConnectAsync(new Node(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Parse("10.255.255.1"), 30303)));
        await Task.Delay(100);
        await host.Shutdown();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dial.IsCompleted, Is.True, "shutdown waits for the dial");
            Assert.That(await dial, Is.False);
            Assert.That(sessions, Is.Zero);
        }
    }

    [Test]
    public async Task Shutdown_closes_the_connections_and_waits_for_them()
    {
        await using IContainer container = CreateFilterContainer(filterEnabled: false, subnetBucketing: false, transport: RlpxTransport.Sockets);
        SocketRlpxHost host = (SocketRlpxHost)container.Resolve<IRlpxHost>();
        TaskCompletionSource<DisconnectEventArgs> disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SessionDisconnected += (_, _, args) => disconnected.TrySetResult(args);
        await host.Init();

        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, host.ListenerEndPoint!.Port));
        Assert.That(() => host.ConnectionCount, Is.EqualTo(1).After(5000, 10));
        await host.Shutdown();

        using CancellationTokenSource timeout = new(Timeout);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(host.ConnectionCount, Is.Zero);
            // The closed connection or the final sweep of shutdown reports it, whichever comes first, as on RlpxHost.
            Assert.That(await disconnected.Task.WaitAsync(timeout.Token), Is.Not.Null, "the session is reported disconnected");
            Assert.That(await ReceiveAsync(client, timeout.Token), Is.Zero, "the client sees the connection close");
        }
    }

    /// <returns>The bytes received, or zero once the remote end closed or reset the connection.</returns>
    private static async Task<int> ReceiveAsync(Socket socket, CancellationToken cancellationToken)
    {
        try
        {
            return await socket.ReceiveAsync(new byte[256], SocketFlags.None, cancellationToken);
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
        {
            return 0;
        }
    }
}
