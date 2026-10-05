// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv5;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv5;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class DiscoveryV5TransportTests
{
    private DiscoveryV5Transport _transport;
    private ConcurrentQueue<(byte[] Data, IPEndPoint Destination)> _sent;

    [SetUp]
    public void Initialize()
    {
        _sent = new();
        _transport = new(new TestLogManager());
        _transport.BindSender((data, destination) =>
        {
            _sent.Enqueue((data, destination));
            return Task.CompletedTask;
        });
    }

    [TearDown]
    public void CleanUp() => _transport.Close();

    [Test]
    public async Task SendsThroughBoundSender()
    {
        byte[] data = [1, 2, 3];
        IPEndPoint to = IPEndPoint.Parse("127.0.0.1:10001");

        await _transport.SendAsync(data, to, CancellationToken.None);

        Assert.That(_sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) sent), Is.True);
        Assert.That(sent.Data, Is.EqualTo(data));
        Assert.That(sent.Destination, Is.EqualTo(to));
    }

    [Test]
    public void DoesNotSendWhenTokenIsAlreadyCanceled()
    {
        byte[] data = [1, 2, 3];
        IPEndPoint to = IPEndPoint.Parse("127.0.0.1:10001");
        using CancellationTokenSource cancellationSource = new();
        cancellationSource.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await _transport.SendAsync(data, to, cancellationSource.Token));

        Assert.That(_sent, Is.Empty);
    }

    [Test]
    public void AddressNotAvailableSendFailureIsTraceOnly([Values] bool traceEnabled)
    {
        TestLogger logger = new() { IsDebug = true, IsTrace = traceEnabled };
        DiscoveryV5Transport transport = new(new OneLoggerLogManager(new ILogger(logger)));
        transport.BindSender((_, _) => Task.FromException(new SocketException((int)SocketError.AddressNotAvailable)));
        IPEndPoint destination = new(IPAddress.Parse("2001:db8::1"), 30303);

        Assert.ThrowsAsync<SocketException>(
            async () => await transport.SendAsync([1, 2, 3], destination, CancellationToken.None));

        if (traceEnabled)
            Assert.That(logger.LogList, Has.Some.EqualTo($"TRACE/ERROR: Failed to send discv5 UDP packet to {destination}"));
        else
            Assert.That(logger.LogList, Is.Empty);
    }

    [Test]
    public async Task ForwardsReceivedMessageToReader()
    {
        byte[] data = [1, 2, 3];
        IPEndPoint from = IPEndPoint.Parse("127.0.0.1:10000");

        using CancellationTokenSource cancellationSource = new(10_000);
        await using IAsyncEnumerator<PooledUdpReceiveResult> enumerator = _transport
            .ReadMessagesAsync(cancellationSource.Token)
            .GetAsyncEnumerator(cancellationSource.Token);
        ValueTask<bool> readTask = enumerator.MoveNextAsync();

        _transport.Receive(PooledUdpReceiveResult.Copy(data, from));

        Assert.That(await readTask, Is.True);
        PooledUdpReceiveResult forwardedPacket = enumerator.Current;

        try
        {
            Assert.That(forwardedPacket.Buffer, Is.SequenceEqualTo(data));
            Assert.That(forwardedPacket.RemoteEndPoint, Is.EqualTo(from));
        }
        finally
        {
            forwardedPacket.Dispose();
        }
    }

    [Test]
    [NonParallelizable]
    public async Task UpdatesDiscoveryBytesSentMetric()
    {
        byte[] sentData = [1, 2, 3, 4];
        IPEndPoint to = IPEndPoint.Parse("127.0.0.1:10001");
        long bytesSentBefore = Interlocked.Read(ref Metrics.DiscoveryBytesSent);

        await _transport.SendAsync(sentData, to, CancellationToken.None);

        Assert.That(_sent, Has.Count.EqualTo(1));
        Assert.That(Interlocked.Read(ref Metrics.DiscoveryBytesSent) - bytesSentBefore, Is.EqualTo(sentData.Length));
    }

    [Test]
    public async Task CloseStopsReader()
    {
        using CancellationTokenSource cancellationSource = new(10_000);
        await using IAsyncEnumerator<PooledUdpReceiveResult> enumerator = _transport
            .ReadMessagesAsync(cancellationSource.Token)
            .GetAsyncEnumerator(cancellationSource.Token);
        ValueTask<bool> readTask = enumerator.MoveNextAsync();

        _transport.Close();

        Assert.That(await readTask.AsTask().WaitAsync(cancellationSource.Token), Is.False);
    }
}
