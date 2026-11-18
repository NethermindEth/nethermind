// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

public class PooledUdpReceiveResultTests
{
    [Test]
    public void Copies_return_the_buffer_once([Values] bool concurrent)
    {
        ArrayPool<byte> pool = Substitute.For<ArrayPool<byte>>();
        pool.Rent(Arg.Any<int>()).Returns(new byte[3]);
        PooledUdpReceiveResult packet = new(new IPEndPoint(IPAddress.Loopback, 30303), new ArrayPoolSpan<byte>(pool, 3));
        PooledUdpReceiveResult copy = packet;
        if (concurrent) Parallel.Invoke(packet.Dispose, copy.Dispose);
        else
        {
            packet.Dispose();
            copy.Dispose();
        }
        pool.Received(1).Return(Arg.Any<byte[]>(), Arg.Any<bool>());
    }

    [Test]
    public void Forwarding_failure_releases_buffer_once([Values] bool callbackDisposes)
    {
        ArrayPool<byte> pool = Substitute.For<ArrayPool<byte>>();
        pool.Rent(Arg.Any<int>()).Returns(new byte[3]);
        using PooledUdpReceiveResult packet = new(new IPEndPoint(IPAddress.Loopback, 30303), new ArrayPoolSpan<byte>(pool, 3));
        DiscoveryHandler handler = new(Substitute.For<IDiscoveryMsgListener>(), Substitute.For<IMessageSerializationService>(),
            Timestamper.Default, LimboLogs.Instance);
        handler.InitializeChannel(Substitute.For<IDatagramSocket>(), forwarded =>
        {
            if (callbackDisposes) forwarded.Dispose();
            throw new InvalidOperationException("forward failed");
        });

        Assert.Throws<InvalidOperationException>(() => handler.Receive(packet));
        if (!callbackDisposes)
        {
            Assert.That(packet.Buffer.Length, Is.EqualTo(3));
            pool.DidNotReceive().Return(Arg.Any<byte[]>(), Arg.Any<bool>());
        }
        packet.Dispose();
        pool.Received(1).Return(Arg.Any<byte[]>(), Arg.Any<bool>());
    }

    [Test]
    public void Successful_forward_keeps_buffer_owned_by_receiver()
    {
        ArrayPool<byte> pool = Substitute.For<ArrayPool<byte>>();
        pool.Rent(Arg.Any<int>()).Returns(new byte[3]);
        using PooledUdpReceiveResult packet = new(new IPEndPoint(IPAddress.Loopback, 30303), new ArrayPoolSpan<byte>(pool, 3));
        DiscoveryHandler handler = new(Substitute.For<IDiscoveryMsgListener>(), Substitute.For<IMessageSerializationService>(),
            Timestamper.Default, LimboLogs.Instance);
        PooledUdpReceiveResult? received = null;
        handler.InitializeChannel(Substitute.For<IDatagramSocket>(), forwarded => received = forwarded);

        handler.Receive(packet);

        Assert.That(received, Is.SameAs(packet));
        Assert.That(received!.Buffer.Length, Is.EqualTo(3));
        pool.DidNotReceive().Return(Arg.Any<byte[]>(), Arg.Any<bool>());
        received.Dispose();
        pool.Received(1).Return(Arg.Any<byte[]>(), Arg.Any<bool>());
    }

    [Test]
    public void Failed_bind_disposes_socket_before_returning_error()
    {
        IDatagramSocket socket = Substitute.For<IDatagramSocket>();
        socket.When(value => value.Bind(Arg.Any<IPEndPoint>())).Do(_ => throw new InvalidOperationException("bind failed"));
        IIPResolver resolver = Substitute.For<IIPResolver>();
        resolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
            new IIPResolver.NethermindIp(IPAddress.Loopback, IPAddress.Loopback)));
        NetworkListenerState state = new(new NetworkConfig { LocalIp = "127.0.0.1" }, resolver, LimboLogs.Instance);
        DiscoveryConnectionsPool connections = new(LimboLogs.Instance.GetClassLogger<DiscoveryConnectionsPool>(), new DiscoveryConfig(), state);

        Assert.Throws<InvalidOperationException>(() => connections.Bind(_ => socket, 30303, packet => packet.Dispose()));

        socket.Received(1).Dispose();
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Receive_callback_failure_releases_buffer([Values] bool forwardingFailure, CancellationToken token)
    {
        IPEndPoint endpoint = new(IPAddress.Loopback, 30303);
        IDatagramSocket socket = Substitute.For<IDatagramSocket>();
        socket.LocalEndpoint.Returns(endpoint);
        int receives = 0;
        socket.ReceiveFromAsync(Arg.Any<Memory<byte>>(), Arg.Any<SocketAddress>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (Interlocked.Increment(ref receives) != 1) return ValueTask.FromException<int>(new ObjectDisposedException("socket"));
            call.Arg<Memory<byte>>().Span[0] = 1;
            return new ValueTask<int>(1);
        });
        IIPResolver resolver = Substitute.For<IIPResolver>();
        resolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
            new IIPResolver.NethermindIp(IPAddress.Loopback, IPAddress.Loopback)));
        NetworkListenerState state = new(new NetworkConfig { LocalIp = "127.0.0.1" }, resolver, LimboLogs.Instance);
        DiscoveryConnectionsPool connections = new(LimboLogs.Instance.GetClassLogger<DiscoveryConnectionsPool>(), new DiscoveryConfig(), state);
        TaskCompletionSource<PooledUdpReceiveResult> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DiscoveryHandler handler = new(Substitute.For<IDiscoveryMsgListener>(), Substitute.For<IMessageSerializationService>(),
            Timestamper.Default, LimboLogs.Instance);
        handler.InitializeChannel(socket, _ => throw new InvalidOperationException("forward failed"));
        bool readableBeforeCleanup = false;
        connections.Bind(_ => socket, endpoint.Port, packet =>
        {
            received.SetResult(packet);
            try
            {
                if (forwardingFailure) handler.Receive(packet);
                else throw new InvalidOperationException("receive failed");
            }
            finally
            {
                readableBeforeCleanup = packet.Buffer.Length == 1;
            }
        });
        PooledUdpReceiveResult captured = await received.Task.WaitAsync(token);
        await connections.StopAsync();

        Assert.That(readableBeforeCleanup, Is.True, "The receive pool must still own the buffer when its callback throws.");
        Assert.That(() => captured.Buffer, Throws.TypeOf<ObjectDisposedException>());
    }
}
