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
using Nethermind.Core.Test.Threading;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv5;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv5;

[NonParallelizable]
[TestFixture]
public class DiscoveryV5TransportTests
{
    private DiscoveryV5Transport _transport;
    private ConcurrentQueue<(byte[] Data, IPEndPoint Destination)> _sent;

    [SetUp]
    public void Initialize()
    {
        RecordingDatagramSocket socket = new();
        _sent = socket.Sent;
        _transport = new(new TestLogManager());
        _transport.BindSocket(socket);
    }

    [TearDown]
    public void CleanUp() => _transport.Close();

    [Test]
    [NonParallelizable]
    public void Full_queue_counts_drops_without_warning_flood()
    {
        TestLogger logger = new() { IsWarn = true, IsTrace = false };
        DiscoveryV5Transport transport = new(new OneLoggerLogManager(new ILogger(logger)))
        {
            ChallengeLimiter = new(int.MaxValue, TimeSpan.FromDays(1)),
            SourceLimiter = new(NodeFilter.AcceptAll)
        };
        long before = Interlocked.Read(ref Metrics.DiscoveryV5MessagesDropped);
        try
        {
            for (int i = 0; i < 1056; i++)
                transport.Receive(PooledUdpReceiveResult.Copy(new byte[] { 1 }, new IPEndPoint(IPAddress.Loopback, 30303)));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Interlocked.Read(ref Metrics.DiscoveryV5MessagesDropped) - before, Is.EqualTo(32));
                Assert.That(logger.LogList, Is.Empty);
            }
        }
        finally
        {
            transport.Close();
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Cheap_packets_do_not_consume_the_expensive_work_budget(CancellationToken token)
    {
        ManualTimeProvider time = new();
        _transport.Close();
        _transport = new(LimboLogs.Instance)
        {
            ChallengeLimiter = new(512, TimeSpan.FromMilliseconds(100), time),
            SourceLimiter = new(32, 8192, TimeSpan.FromMilliseconds(100), time)
        };
        Task<int> received = Drain(token);
        for (int i = 0; i < 513; i++)
        {
            IPEndPoint source = new(new IPAddress(new byte[] { 8, 8, (byte)(i >> 8), (byte)i }), 30303);
            _transport.Receive(PooledUdpReceiveResult.Copy(new byte[] { 1 }, source));
        }
        _transport.Close();
        Assert.That(await received, Is.EqualTo(513));

    }

    [Test]
    [CancelAfter(10000)]
    public async Task Source_limits_share_ipv6_prefixes_and_expire(CancellationToken token)
    {
        ManualTimeProvider time = new();
        _transport.Close();
        _transport = new(LimboLogs.Instance)
        {
            ChallengeLimiter = new(2, TimeSpan.FromMilliseconds(100), time),
            SourceLimiter = new(1, 8, TimeSpan.FromMilliseconds(100), time)
        };
        Task<int> received = Drain(token);
        Send("2001:4860:1:2::1");
        Send("2001:4860:1:2::2");
        Send("8.8.8.8");
        Send("8.8.4.4");
        time.Advance(TimeSpan.FromMilliseconds(99));
        Send("8.8.8.8");
        time.Advance(TimeSpan.FromMilliseconds(1));
        Send("8.8.8.8");
        Send("8.8.4.4");
        _transport.Close();
        Assert.That(await received, Is.EqualTo(5));

        void Send(string address) => _transport.Receive(PooledUdpReceiveResult.Copy(
            new byte[] { 1 }, new IPEndPoint(IPAddress.Parse(address), 30303)));
    }

    private async Task<int> Drain(CancellationToken token)
    {
        int count = 0;
        await foreach (PooledUdpReceiveResult packet in _transport.ReadMessagesAsync(token))
        {
            packet.Dispose();
            count++;
        }
        return count;
    }

    [Test]
    public async Task SendsThroughBoundSocket()
    {
        byte[] data = [1, 2, 3];
        IPEndPoint to = IPEndPoint.Parse("127.0.0.1:10001");

        await _transport.SendAsync(data, to, CancellationToken.None);

#pragma warning disable NUnit2045 // The call establishes an output or mutable state needed by later assertions; keep the success prerequisite fail-fast.
        Assert.That(_sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) sent), Is.True);
#pragma warning restore NUnit2045
        Assert.That(sent.Data, Is.EqualTo(data));
        Assert.That(sent.Destination, Is.EqualTo(to));
    }

    [Test]
    public async Task DoesNotSendWhenTokenIsAlreadyCanceled()
    {
        byte[] data = [1, 2, 3];
        IPEndPoint to = IPEndPoint.Parse("127.0.0.1:10001");
        using CancellationTokenSource cancellationSource = new();
        cancellationSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await _transport.SendAsync(data, to, cancellationSource.Token));

        Assert.That(_sent, Is.Empty);
    }

    [Test]
    public async Task AddressNotAvailableSendFailureIsTraceOnly([Values] bool traceEnabled)
    {
        TestLogger logger = new() { IsDebug = true, IsTrace = traceEnabled };
        DiscoveryV5Transport transport = new(new OneLoggerLogManager(new ILogger(logger)));
        IDatagramSocket socket = Substitute.For<IDatagramSocket>();
        socket.SendToAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<IPEndPoint>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException(new SocketException((int)SocketError.AddressNotAvailable)));
        transport.BindSocket(socket);
        IPEndPoint destination = new(IPAddress.Parse("2001:db8::1"), 30303);

        await Assert.ThrowsAsync<SocketException>(
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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(forwardedPacket.Buffer, Is.SequenceEqualTo(data));
                Assert.That(forwardedPacket.RemoteEndPoint, Is.EqualTo(from));
            }
        }
        finally
        {
            forwardedPacket.Dispose();
        }
    }

    [Test]
    public async Task Packet_traces_mask_both_endpoints([Values] bool maskSensitiveData)
    {
        bool previous = SensitiveLogMasking.Enabled;
        SensitiveLogMasking.Enabled = maskSensitiveData;
        TestLogger logger = new();
        DiscoveryV5Transport transport = new(new OneLoggerLogManager(new ILogger(logger)));
        transport.BindSocket(new RecordingDatagramSocket());
        IPEndPoint endpoint = IPEndPoint.Parse("192.0.2.31:31417");
        try
        {
            transport.Receive(PooledUdpReceiveResult.Copy([1, 2, 3], endpoint));
            await transport.SendAsync([1, 2, 3], endpoint, CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(logger.LogList, Has.Count.EqualTo(2));
                Assert.That(logger.LogList, Has.All.Contains(maskSensitiveData ? "[redacted]" : endpoint.ToString()));
                if (maskSensitiveData) Assert.That(logger.LogList, Has.None.Contains(endpoint.ToString()));
            }
        }
        finally
        {
            transport.Close();
            SensitiveLogMasking.Enabled = previous;
        }
    }

    [Test]
    public async Task UpdatesDiscoveryBytesSentMetric()
    {
        byte[] sentData = [1, 2, 3, 4];
        IPEndPoint to = IPEndPoint.Parse("127.0.0.1:10001");
        long bytesSentBefore = Interlocked.Read(ref Metrics.DiscoveryBytesSent);

        await _transport.SendAsync(sentData, to, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sent, Has.Count.EqualTo(1));
            Assert.That(Interlocked.Read(ref Metrics.DiscoveryBytesSent) - bytesSentBefore, Is.EqualTo(sentData.Length));
        }
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
