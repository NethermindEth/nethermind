// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Network.Rlpx.Sockets;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;
using Snappier;
using static Nethermind.Network.Rlpx.FrameEncoder;

namespace Nethermind.Network.Test.Rlpx.Sockets;

[Parallelizable(ParallelScope.All)]
public class SocketRlpxConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task Receives_framed_chunked_and_snappy_packets_split_anywhere([Values(1, 7, 16, 23, int.MaxValue)] int maxReceiveLength)
    {
        await using ConnectionUnderTest test = await ConnectionUnderTest.StartAsync(maxReceiveLength: maxReceiveLength);
        byte[] small = Content(100, 1);
        byte[] chunked = Content(3000, 2);
        byte[] compressed = Content(5000, 3);

        await test.Peer.SendAsync(test.Peer.Encode(0x10, small, EncodingMode.Framed));
        await test.Peer.SendAsync(test.Peer.Encode(0x11, chunked, EncodingMode.Framed));
        await test.WaitForEventsAsync(2);
        test.Connection.EnableSnappy();
        await test.Peer.SendAsync(test.Peer.Encode(0x12, compressed, EncodingMode.Snappy));
        await test.WaitForEventsAsync(3);

        Assert.That(test.Packets, Is.EqualTo(new (byte, byte[])[] { (0x10, small), (0x11, chunked), (0x12, compressed) }));
        await test.StopAndAssertBuffersReturnedAsync();
    }

    [Test]
    public async Task Completes_the_handshake_before_init_and_decodes_bytes_that_follow_the_auth()
    {
        await using ConnectionUnderTest test = ConnectionUnderTest.Create();
        byte[] content = Content(50, 1);
        test.Start();

        // The AUTH and the first frame arrive in one read.
        await test.Peer.SendAsync([.. FakeHandshakeService.Message, .. test.Peer.Encode(0x10, content, EncodingMode.Framed)]);
        await test.WaitForEventsAsync(1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(test.Handshake.Received, Is.EqualTo(new[] { FakeHandshakeService.Message }), "the size prefix reaches the handshake service");
            Assert.That(test.Packets, Is.EqualTo(new (byte, byte[])[] { (0x10, content) }));
        }

        Received.InOrder(() =>
        {
            test.Session.Handshake(TestItem.PublicKeyB);
            test.Session.Init(5, test.Connection);
        });
        test.Session.Received(1).Handshake(Arg.Any<PublicKey>());
        await test.StopAndAssertBuffersReturnedAsync();
    }

    [Test]
    public async Task Stops_decoding_after_a_frame_fails_authentication(
        [Values] bool header,
        [Values] bool privileged,
        [Values(5, int.MaxValue)] int maxReceiveLength)
    {
        await using ConnectionUnderTest test = await ConnectionUnderTest.StartAsync(privileged: privileged, maxReceiveLength: maxReceiveLength);
        // Frames are encoded in the order they are sent, since each one advances the peer's cipher and MAC.
        byte[] first = test.Peer.Encode(0x10, Content(100, 1), EncodingMode.Framed);
        byte[] corrupted = test.Peer.Encode(0x11, Content(100, 2), EncodingMode.Framed);
        byte[] last = test.Peer.Encode(0x12, Content(100, 3), EncodingMode.Framed);
        // A header MAC byte, or the payload MAC's last byte.
        corrupted[header ? Frame.HeaderSize : ^1] ^= 0xFF;

        await test.Peer.SendAsync([.. first, .. corrupted, .. last]);
        string[] expected = privileged ? ["receive 16"] : ["receive 16", "disconnect Exception"];
        await test.WaitForEventsAsync(expected.Length);
        await Task.Delay(200);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(test.Events, Is.EqualTo(expected), "nothing after the failed frame is decoded");
            Assert.That(test.Connection.Closed.IsCompleted, Is.False, "the transport leaves closing to the session");
        }

        Assert.That(() => test.Pool.OutstandingExceptSendBatch, Is.Empty.After(5000, 10), "the frame buffers are released while the connection stays open");
        await test.StopAndAssertBuffersReturnedAsync();
    }

    [Test]
    public async Task Reports_a_merger_error_and_keeps_decoding([Values] bool privileged)
    {
        await using ConnectionUnderTest test = await ConnectionUnderTest.StartAsync(privileged: privileged);
        // An authenticated frame whose header body is not a well formed RLP list.
        byte[] header = new byte[Frame.HeaderSize];
        header[2] = Frame.BlockSize;
        header[3] = 0xf7;
        byte[] next = Content(100, 1);

        await test.Peer.SendAsync([.. test.Peer.EncryptFrame(header, new byte[Frame.BlockSize]), .. test.Peer.Encode(0x10, next, EncodingMode.Framed)]);
        string[] expected = privileged ? ["receive 16"] : ["disconnect Exception", "receive 16"];
        await test.WaitForEventsAsync(expected.Length);

        Assert.That(test.Events, Is.EqualTo(expected));
        await test.StopAndAssertBuffersReturnedAsync();
    }

    [Test]
    public async Task Handles_a_packet_received_before_a_reset_first()
    {
        TaskCompletionSource decoding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ConnectionUnderTest test = await ConnectionUnderTest.StartAsync(beforeDecoding: () => new ValueTask(decoding.Task));
        byte[] packet = test.Peer.Encode(0x01, Content(10, 1), EncodingMode.Framed);
        long received = test.Socket.Received;

        await test.Peer.SendAsync(packet);
        Assert.That(() => test.Socket.Received, Is.EqualTo(received + packet.Length).After(5000, 10), "the packet is in the pipe");
        test.Socket.FailReceive(new SocketException((int)SocketError.ConnectionReset));
        await test.Connection.Closed.WaitAsync(Timeout);
        decoding.SetResult();
        await test.WaitForEventsAsync(2);

        Assert.That(test.Events, Is.EqualTo(new[] { "receive 1", "disconnect ConnectionReset" }));
        await test.StopAndAssertBuffersReturnedAsync();
    }

    [Test]
    public async Task Handles_an_error_raised_by_a_packet_before_the_next_packet()
    {
        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ConnectionUnderTest test = await ConnectionUnderTest.StartAsync(onReceive: packet =>
        {
            // Holds the processing thread, so that the next two packets wait in its queue.
            if (packet.PacketType == 0x20) held.Task.Wait();
        });
        test.Connection.EnableSnappy();
        // Declares 5 bytes, then copies from before the start of the output.
        byte[] invalidSnappy = [0x05, 0xFF, 0xFF];

        await test.Peer.SendAsync([
            .. test.Peer.Encode(0x20, Content(10, 1), EncodingMode.Snappy),
            .. test.Peer.Encode(0x21, invalidSnappy, EncodingMode.Framed),
            .. test.Peer.Encode(0x22, Content(10, 2), EncodingMode.Snappy)]);
        Assert.That(() => test.Socket.Received, Is.GreaterThan(FakeHandshakeService.Message.Length).After(5000, 10));
        await Task.Delay(100);
        held.SetResult();
        await test.WaitForEventsAsync(3);

        Assert.That(test.Events, Is.EqualTo(new[] { "receive 32", "disconnect Exception", "receive 34" }));
        await test.StopAndAssertBuffersReturnedAsync();
    }

    [Test]
    public async Task Releases_a_partly_received_packet_at_the_end_of_the_stream()
    {
        await using ConnectionUnderTest test = await ConnectionUnderTest.StartAsync();
        byte[] frames = test.Peer.Encode(0x10, Content(3000, 1), EncodingMode.Framed);
        const int firstFrameLength = Frame.HeaderSize + Frame.MacSize + Frame.DefaultMaxFrameSize + Frame.MacSize;

        await test.Peer.SendAsync(frames[..firstFrameLength]);
        test.Peer.Socket.Close();
        await test.Connection.Completion.WaitAsync(Timeout);

        Assert.That(test.Events, Is.Empty, "the end of the stream is not an error");
        await test.StopAndAssertBuffersReturnedAsync();
    }

    [Test]
    public async Task Closes_after_the_read_timeout([Values] bool privileged)
    {
        await using ConnectionUnderTest test = await ConnectionUnderTest.StartAsync(privileged: privileged, readTimeout: TimeSpan.FromMilliseconds(200));

        await test.Connection.Closed.WaitAsync(Timeout);
        await test.Connection.Completion.WaitAsync(Timeout);
        await Task.Delay(100);

        Assert.That(test.Events, Is.EqualTo(privileged ? Array.Empty<string>() : ["disconnect Exception"]));
        await test.StopAndAssertBuffersReturnedAsync();
    }

    [Test]
    public async Task Closes_when_the_handshake_times_out()
    {
        await using ConnectionUnderTest test = ConnectionUnderTest.Create();
        long timeouts = Metrics.HandshakeTimeouts;
        test.Start();

        await test.Connection.Closed.WaitAsync(Timeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.HandshakeTimeouts, Is.GreaterThan(timeouts));
            test.Session.DidNotReceive().Handshake(Arg.Any<PublicKey>());
        }

        await test.StopAndAssertBuffersReturnedAsync();
    }

    [Test]
    public async Task Disconnects_for_a_snappy_packet_over_the_size_limit()
    {
        await using ConnectionUnderTest test = await ConnectionUnderTest.StartAsync();
        test.Connection.EnableSnappy();
        // The Snappy length prefix declares 17 MiB.
        byte[] content = [0x80, 0x80, 0x80, 0x11, 0x00];

        await test.Peer.SendAsync(test.Peer.Encode(0x10, content, EncodingMode.Framed));
        await test.WaitForEventsAsync(1);

        Assert.That(test.Events, Is.EqualTo(new[] { "disconnect BreachOfProtocol" }));
        await test.StopAndAssertBuffersReturnedAsync();
    }

    [Test]
    public async Task Keeps_a_leased_packet_readable_after_the_connection_stops([Values] bool snappy)
    {
        IMemoryOwner<byte>? lease = null;
        await using ConnectionUnderTest test = await ConnectionUnderTest.StartAsync(onReceive: packet => lease = packet.LeaseContent());
        byte[] content = Content(100, 1);
        if (snappy) test.Connection.EnableSnappy();

        await test.Peer.SendAsync(test.Peer.Encode(0x10, content, snappy ? EncodingMode.Snappy : EncodingMode.Framed));
        await test.WaitForEventsAsync(1);
        await test.StopAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lease!.Memory.ToArray(), Is.EqualTo(content));
            Assert.That(test.Pool.Outstanding, Has.Length.EqualTo(1), "the lease holds its buffer");
        }

        lease.Dispose();
        await test.StopAndAssertBuffersReturnedAsync();
    }

    [Test]
    public async Task Sends_each_thread_s_packets_in_order()
    {
        const int Threads = 4;
        const int PacketsPerThread = 100;
        await using ConnectionUnderTest test = await ConnectionUnderTest.StartAsync();

        Task[] senders = Enumerable.Range(0, Threads).Select(thread => Task.Run(() =>
        {
            for (int i = 0; i < PacketsPerThread; i++)
            {
                test.Enqueue([(byte)thread, (byte)i, .. Content(100 * (i % 20), i)]);
            }
        })).ToArray();
        await Task.WhenAll(senders);

        int[] next = new int[Threads];
        for (int i = 0; i < Threads * PacketsPerThread; i++)
        {
            (_, byte[] content) = await test.Peer.ReceivePacketAsync(test.Cancellation);
            Assert.That(content[1], Is.EqualTo(next[content[0]]++), $"thread {content[0]}'s packets arrive in order");
        }

        await test.StopAndAssertBuffersReturnedAsync();
    }

    [Test]
    public async Task Sends_a_packet_larger_than_the_batch_and_the_one_after_it([Values(1, 7, 1000, int.MaxValue)] int maxSendLength)
    {
        await using ConnectionUnderTest test = await ConnectionUnderTest.StartAsync();
        test.Socket.MaxSendLength = maxSendLength;
        byte[] large = Content(SocketRlpxConnection.SendBatchSize + 1000, 1);
        byte[] small = Content(10, 2);

        test.Enqueue(small);
        test.Enqueue(large);
        test.Enqueue(small);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((await test.Peer.ReceivePacketAsync(test.Cancellation)).Content, Is.EqualTo(small));
            Assert.That((await test.Peer.ReceivePacketAsync(test.Cancellation)).Content, Is.EqualTo(large));
            Assert.That((await test.Peer.ReceivePacketAsync(test.Cancellation)).Content, Is.EqualTo(small));
        }

        await test.StopAndAssertBuffersReturnedAsync();
    }

    [Test]
    public async Task Sends_the_handshake_without_the_simulated_latency([Values] HandshakeRole role)
    {
        await using ConnectionUnderTest test = ConnectionUnderTest.Create(role, sendLatency: TimeSpan.FromSeconds(30));
        using CancellationTokenSource quickly = new(TimeSpan.FromSeconds(3));
        test.Start();

        if (role == HandshakeRole.Recipient)
        {
            await test.Peer.SendAsync(FakeHandshakeService.Message);
        }

        Assert.That(await test.Peer.ReceiveHandshakeAsync(quickly.Token), Is.EqualTo(FakeHandshakeService.Message));
        await test.StopAndAssertBuffersReturnedAsync();
    }

    [Test]
    public async Task Keeps_the_encoding_mode_a_packet_was_queued_with()
    {
        await using ConnectionUnderTest test = await ConnectionUnderTest.StartAsync(sendLatency: TimeSpan.FromMilliseconds(300));
        byte[] framed = Content(100, 1);
        byte[] compressed = Content(100, 2);

        test.Enqueue(framed);
        test.Connection.EnableSnappy();
        test.Enqueue(compressed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((await test.Peer.ReceivePacketAsync(test.Cancellation)).Content, Is.EqualTo(framed));
            Assert.That(Snappy.DecompressToArray((await test.Peer.ReceivePacketAsync(test.Cancellation)).Content), Is.EqualTo(compressed));
        }

        await test.StopAndAssertBuffersReturnedAsync();
    }

    [Test]
    public async Task Stops_taking_packets_at_the_high_watermark_until_they_are_sent()
    {
        TaskCompletionSource sending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ConnectionUnderTest test = await ConnectionUnderTest.StartAsync();
        test.Socket.SendGate = sending.Task;
        byte[] content = Content(100_000, 1);

        int queued = 0;
        while (test.Connection.IsWritable && queued < 100)
        {
            Assert.That(test.Enqueue(content), Is.EqualTo(content.Length + 1));
            queued++;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queued, Is.EqualTo(30), "3,000,000 bytes of 100,001-byte packets");
            Assert.That(test.Enqueue(content), Is.Zero, "a packet is dropped while the connection is not writable");
        }

        sending.SetResult();
        for (int i = 0; i < queued; i++)
        {
            await test.Peer.ReceivePacketAsync(test.Cancellation);
        }

        Assert.That(() => test.Connection.IsWritable, Is.True.After(5000, 10));
        await test.Connection.DisconnectAsync();
        Assert.That(test.Connection.IsWritable, Is.False, "a closed connection takes nothing");
        await test.StopAndAssertBuffersReturnedAsync();
    }

    [Test]
    public async Task Releases_the_packets_still_queued_when_the_connection_closes()
    {
        TaskCompletionSource sending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ConnectionUnderTest test = await ConnectionUnderTest.StartAsync();
        test.Socket.SendGate = sending.Task;

        for (int i = 0; i < 5; i++)
        {
            test.Enqueue(Content(100, i));
        }

        await test.Connection.DisconnectAsync();
        sending.SetResult();
        await test.StopAndAssertBuffersReturnedAsync();
    }

    [Test]
    public async Task Releases_a_packet_serialized_while_the_connection_closes()
    {
        await using ConnectionUnderTest test = await ConnectionUnderTest.StartAsync();
        byte[] content = Content(100, 1);

        int length = test.Connection.Enqueue(new PayloadMessage(content, () => test.Connection.DisconnectAsync()) { AdaptivePacketType = 0x10 });

        Assert.That(length, Is.EqualTo(content.Length + 1), "as on DotNetty, the packet counts as sent");
        await test.StopAndAssertBuffersReturnedAsync();
    }

    [Test]
    public async Task Closes_without_reporting_a_failed_send()
    {
        await using ConnectionUnderTest test = await ConnectionUnderTest.StartAsync();
        test.Socket.SendError = new SocketException((int)SocketError.ConnectionReset);

        test.Enqueue(Content(10, 1));
        await test.Connection.Closed.WaitAsync(Timeout);
        await test.Connection.Completion.WaitAsync(Timeout);

        Assert.That(test.Events, Is.Empty);
        await test.StopAndAssertBuffersReturnedAsync();
    }

    private static byte[] Content(int length, int seed)
    {
        byte[] content = new byte[length];
        new Random(seed).NextBytes(content);
        return content;
    }

    private sealed class PayloadMessage(byte[] payload, Action? onSerialize = null) : P2PMessage
    {
        public override int PacketType => 0x10;

        public override string Protocol => "test";

        public byte[] Payload => payload;

        public Action? OnSerialize => onSerialize;
    }

    private sealed class PayloadSerializer : IMessageSerializer<PayloadMessage>
    {
        public int GetLength(PayloadMessage message) => message.Payload.Length;

        public int Serialize(Span<byte> destination, PayloadMessage message)
        {
            message.OnSerialize?.Invoke();
            message.Payload.CopyTo(destination);
            return message.Payload.Length;
        }

        public PayloadMessage Deserialize(ReadOnlySpan<byte> data) => throw new NotSupportedException();
    }

    /// <summary>A connection on an in-memory socket, its scripted peer, and a session that records what it is given.</summary>
    private sealed class ConnectionUnderTest : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new(Timeout);
        private readonly HandshakeRole _role;
        private bool _stopped;

        private ConnectionUnderTest(
            HandshakeRole role,
            bool privileged,
            int maxReceiveLength,
            TimeSpan sendLatency,
            TimeSpan? readTimeout,
            Func<ValueTask>? beforeDecoding,
            Action<ZeroPacket>? onReceive)
        {
            _role = role;
            (EncryptionSecrets initiator, EncryptionSecrets recipient) = NetTestVectors.GetSecretsPair();
            (EncryptionSecrets local, EncryptionSecrets remote) = role == HandshakeRole.Initiator ? (initiator, recipient) : (recipient, initiator);
            (Socket, InMemoryRlpxSocket peerSocket) = InMemoryRlpxSocket.CreatePair();
            Socket.MaxReceiveLength = maxReceiveLength;
            Peer = new ScriptedRlpxPeer(peerSocket, remote);
            Handshake = new FakeHandshakeService(local, TestItem.PublicKeyB);
            Threads = new RlpxProcessingThreads(1, LimboLogs.Instance.GetClassLogger<SocketRlpxConnectionTests>());
            Connection = new SocketRlpxConnection(
                Socket,
                role,
                Handshake,
                new MessageSerializationService(SerializerInfo.Create(new PayloadSerializer())),
                Threads.Next(),
                LimboLogs.Instance,
                sendLatency,
                ZeroFrameDecoder.DefaultMaxInboundFrameSize,
                Pool)
            {
                ReadTimeout = readTimeout ?? SocketRlpxConnection.DefaultReadTimeout,
                BeforeDecoding = beforeDecoding
            };

            Session = Substitute.For<ISession>();
            Session.Node.Returns(new Node(TestItem.PublicKeyB, "127.0.0.1", 30303, isStatic: privileged));
            Session.RemoteNodeId.Returns(TestItem.PublicKeyB);
            Session.When(static s => s.ReceiveMessage(Arg.Any<ZeroPacket>())).Do(call =>
            {
                ZeroPacket packet = call.Arg<ZeroPacket>();
                onReceive?.Invoke(packet);
                Packets.Enqueue((packet.PacketType, packet.Content.ToArray()));
                Events.Enqueue($"receive {packet.PacketType}");
            });
            Session.When(static s => s.InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>()))
                .Do(call => Events.Enqueue($"disconnect {call.ArgAt<DisconnectReason>(0)}"));
            Session.When(static s => s.Init(Arg.Any<byte>(), Arg.Any<IPacketSender>())).Do(_ => Initialized.TrySetResult());
        }

        public SocketRlpxConnection Connection { get; }

        public InMemoryRlpxSocket Socket { get; }

        public ScriptedRlpxPeer Peer { get; }

        public FakeHandshakeService Handshake { get; }

        public ISession Session { get; }

        public RlpxProcessingThreads Threads { get; }

        public TrackingArrayPool Pool { get; } = new();

        public ConcurrentQueue<string> Events { get; } = new();

        public ConcurrentQueue<(byte, byte[])> Packets { get; } = new();

        public TaskCompletionSource Initialized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Cancellation => _cancellation.Token;

        public static ConnectionUnderTest Create(
            HandshakeRole role = HandshakeRole.Recipient,
            bool privileged = false,
            int maxReceiveLength = int.MaxValue,
            TimeSpan sendLatency = default,
            TimeSpan? readTimeout = null,
            Func<ValueTask>? beforeDecoding = null,
            Action<ZeroPacket>? onReceive = null)
            => new(role, privileged, maxReceiveLength, sendLatency, readTimeout, beforeDecoding, onReceive);

        /// <summary>Starts the connection and completes the handshake as the peer.</summary>
        public static async Task<ConnectionUnderTest> StartAsync(
            bool privileged = false,
            int maxReceiveLength = int.MaxValue,
            TimeSpan sendLatency = default,
            TimeSpan? readTimeout = null,
            Func<ValueTask>? beforeDecoding = null,
            Action<ZeroPacket>? onReceive = null)
        {
            ConnectionUnderTest test = Create(HandshakeRole.Recipient, privileged, maxReceiveLength, sendLatency, readTimeout, beforeDecoding, onReceive);
            test.Start();
            await test.Peer.SendAsync(FakeHandshakeService.Message);
            await test.Peer.ReceiveHandshakeAsync(test.Cancellation);
            await test.Initialized.Task.WaitAsync(Timeout);
            return test;
        }

        public void Start() => Connection.Start(Session);

        public int Enqueue(byte[] content) => Connection.Enqueue(new PayloadMessage(content) { AdaptivePacketType = 0x10 });

        public async Task WaitForEventsAsync(int count)
        {
            while (Events.Count < count)
            {
                await Task.Delay(5, Cancellation);
            }
        }

        /// <summary>Closes the connection and waits for its loops and processing thread to finish.</summary>
        public async Task StopAsync()
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            await Connection.DisconnectAsync();
            await Connection.Completion.WaitAsync(Timeout);
            await Threads.StopAsync().WaitAsync(Timeout);
            Peer.Socket.Close();
        }

        public async Task StopAndAssertBuffersReturnedAsync()
        {
            await StopAsync();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Pool.Outstanding, Is.Empty, "every rented buffer is returned");
                Assert.That(Pool.DoubleReturns, Is.Zero, "no buffer is returned twice");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _cancellation.Dispose();
        }
    }
}
