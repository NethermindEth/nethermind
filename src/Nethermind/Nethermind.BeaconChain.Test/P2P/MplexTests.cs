// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Quic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Core.Test.Threading;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols;
using Nethermind.Network.Libp2p;
using NSubstitute;
using Channel = Nethermind.Libp2p.Core.Channel;

namespace Nethermind.BeaconChain.Test.P2P;

public class MplexTests
{
    [Test]
    [CancelAfter(10_000)]
    public async Task Both_sides_can_open_stream_zero_and_close_only_one_direction(CancellationToken token)
    {
        await using RawConnection peer = new();
        peer.Requests.Add(new UpgradeOptions(), token);
        (ulong id, byte flag, byte[] data) = await peer.ReadFrameAsync(token);
        Assert.That((id, flag, data), Is.EqualTo((0UL, (byte)0, Array.Empty<byte>())));
        IChannel local = await peer.Opened.Reader.ReadAsync(token);
        await peer.WriteFrameAsync(0, 0, [], token);
        IChannel remote = await peer.Opened.Reader.ReadAsync(token);

        await peer.WriteFrameAsync(0, 1, [0xa1], token);
        await peer.WriteFrameAsync(0, 2, [0xb2], token);
        Assert.That((await local.ReadAsync(1, token: token).OrThrow()).ToArray(), Is.EqualTo(new byte[] { 0xa1 }));
        Assert.That((await remote.ReadAsync(1, token: token).OrThrow()).ToArray(), Is.EqualTo(new byte[] { 0xb2 }));
        await peer.WriteFrameAsync(0, 4, [], token);
        Assert.That((await remote.ReadAsync(1, token: token)).Result, Is.EqualTo(IOResult.Ended));

        Task<IOResult> send = remote.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 0xc3 }), token).AsTask();
        Assert.That(await peer.ReadFrameAsync(token), Is.EqualTo((0UL, (byte)1, new byte[] { 0xc3 })));
        Assert.That(await send, Is.EqualTo(IOResult.Ok));
        await peer.WriteFrameAsync(0, 1, [0xd4], token);
        Assert.That((await local.ReadAsync(1, token: token).OrThrow()).ToArray(), Is.EqualTo(new byte[] { 0xd4 }));
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Half_close_drains_prior_data_and_keeps_the_response_direction_open(CancellationToken token)
    {
        await using RawConnection peer = new();
        await peer.WriteFrameAsync(23, 0, Encoding.UTF8.GetBytes("debug name"), token);
        IChannel application = await peer.Opened.Reader.ReadAsync(token);
        await peer.WriteFrameAsync(23, 2, [1, 2], token);
        await peer.WriteFrameAsync(23, 2, [3], token);
        await peer.WriteFrameAsync(23, 4, [], token);
        Assert.That((await application.ReadAsync(3, token: token).OrThrow()).ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That((await application.ReadAsync(1, token: token)).Result, Is.EqualTo(IOResult.Ended));

        Task<IOResult> send = application.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 4, 5 }), token).AsTask();
        Assert.That(await peer.ReadFrameAsync(token), Is.EqualTo((23UL, (byte)1, new byte[] { 4, 5 })));
        await send;
        await application.WriteEofAsync(token);
        Assert.That(await peer.ReadFrameAsync(token), Is.EqualTo((23UL, (byte)3, Array.Empty<byte>())));
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Reset_discards_unread_data_and_fails_future_reads_and_writes(CancellationToken token)
    {
        await using RawConnection peer = new();
        await peer.WriteFrameAsync(0, 0, [], token);
        IChannel application = await peer.Opened.Reader.ReadAsync(token);
        await peer.WriteFrameAsync(0, 2, [1, 2, 3], token);
        await peer.WriteFrameAsync(0, 6, [], token);
        await application;
        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That((await application.ReadAsync(1, token: token)).Result, Is.EqualTo(IOResult.Aborted));
        Assert.That(await application.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 4 }), token), Is.Not.EqualTo(IOResult.Ok));
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Slow_stream_is_reset_while_another_stream_still_receives_data(CancellationToken token)
    {
        await using RawConnection peer = new();
        await peer.WriteFrameAsync(0, 0, [], token);
        IChannel slow = await peer.Opened.Reader.ReadAsync(token);
        await peer.WriteFrameAsync(0, 2, new byte[MplexProtocol.MaxMessageSize], token);
        Task overflow = peer.WriteFrameAsync(0, 2, [9], token);
        Assert.That(await peer.ReadFrameAsync(token), Is.EqualTo((0UL, (byte)5, Array.Empty<byte>())));
        await overflow;
        await peer.WriteFrameAsync(1, 0, [], token);
        IChannel healthy = await peer.Opened.Reader.ReadAsync(token);
        await peer.WriteFrameAsync(1, 2, [0x75], token);
        Assert.That((await healthy.ReadAsync(1, token: token).OrThrow()).ToArray(), Is.EqualTo(new byte[] { 0x75 }));
        Assert.That((await slow.ReadAsync(1, token: token)).Result, Is.EqualTo(IOResult.Aborted));
    }

    [Test]
    [CancelAfter(15_000)]
    public async Task Active_stream_limit_includes_unread_streams_and_shutdown_joins_their_pumps(CancellationToken token)
    {
        await using RawConnection peer = new();
        for (ulong id = 0; id < MplexProtocol.MaxStreams; id++)
        {
            await peer.WriteFrameAsync(id, 0, [], token);
            await peer.Opened.Reader.ReadAsync(token);
        }
        Task excess = peer.WriteFrameAsync(MplexProtocol.MaxStreams, 0, [], token);
        Assert.That(await peer.ReadFrameAsync(token), Is.EqualTo(((ulong)MplexProtocol.MaxStreams, (byte)5, Array.Empty<byte>())));
        await excess;
        await peer.Transport.AbortAsync();
        await peer.Runner.WaitAsync(token);
    }

    [Test]
    [CancelAfter(15_000)]
    public async Task Connection_budget_includes_payloads_blocked_in_application_delivery(CancellationToken token)
    {
        await using RawConnection peer = new();
        int count = MplexProtocol.MaxBufferedBytes / MplexProtocol.MaxMessageSize;
        for (ulong id = 0; id <= (ulong)count; id++)
        {
            await peer.WriteFrameAsync(id, 0, [], token);
            await peer.Opened.Reader.ReadAsync(token);
            if (id == (ulong)count)
            {
                Task excess = peer.WriteFrameAsync(id, 2, [7], token);
                Assert.That(await peer.ReadFrameAsync(token), Is.EqualTo((id, (byte)5, Array.Empty<byte>())));
                await excess;
            }
            else await peer.WriteFrameAsync(id, 2, new byte[MplexProtocol.MaxMessageSize], token);
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Empty_data_frames_do_not_fill_the_input_queue(CancellationToken token)
    {
        await using RawConnection peer = new();
        await peer.WriteFrameAsync(0, 0, [], token);
        IChannel application = await peer.Opened.Reader.ReadAsync(token);
        for (int index = 0; index < 256; index++) await peer.WriteFrameAsync(0, 2, [], token);
        await peer.WriteFrameAsync(0, 2, [6], token);
        Assert.That((await application.ReadAsync(1, token: token).OrThrow()).ToArray(), Is.EqualTo(new byte[] { 6 }));
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Frames_can_be_fragmented_or_coalesced_and_unknown_payload_is_fully_drained([Values] bool fragment, CancellationToken token)
    {
        await using RawConnection peer = new();
        // Unknown data(stream 8), NewStream(128), data(stream 128), CloseInitiator(128).
        byte[] wire = [0x42, 3, 0x90, 0x91, 0x92, 0x80, 0x08, 0, 0x82, 0x08, 2, 0x35, 0x36, 0x84, 0x08, 0];
        if (fragment)
        {
            foreach (byte value in wire) await peer.Transport.WriteAsync(new ReadOnlySequence<byte>(new[] { value }), token).OrThrow();
        }
        else await peer.Transport.WriteAsync(new ReadOnlySequence<byte>(wire), token).OrThrow();
        IChannel application = await peer.Opened.Reader.ReadAsync(token);
        Assert.That((await application.ReadAsync(2, token: token).OrThrow()).ToArray(), Is.EqualTo(new byte[] { 0x35, 0x36 }));
        Assert.That((await application.ReadAsync(1, token: token)).Result, Is.EqualTo(IOResult.Ended));
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Stream_ids_are_full_width_and_not_required_to_be_monotonic(
        [Values(0UL, 127UL, 128UL, 16383UL, 16384UL, (1UL << 60) - 1)] ulong id, CancellationToken token)
    {
        await using RawConnection peer = new();
        await peer.WriteFrameAsync(id, 0, [], token);
        IChannel application = await peer.Opened.Reader.ReadAsync(token);
        await peer.WriteFrameAsync(id, 2, [0x7a], token);
        Assert.That((await application.ReadAsync(1, token: token).OrThrow()).ToArray(), Is.EqualTo(new byte[] { 0x7a }));
        if (id != 0)
        {
            await peer.WriteFrameAsync(0, 0, [], token);
            IChannel earlier = await peer.Opened.Reader.ReadAsync(token);
            await peer.WriteFrameAsync(0, 2, [0x7b], token);
            Assert.That((await earlier.ReadAsync(1, token: token).OrThrow()).ToArray(), Is.EqualTo(new byte[] { 0x7b }));
        }
    }

    private static IEnumerable<byte[]> MalformedFrames =>
    [
        [0x80, 0], // nonminimal header
        [7], // reserved tag
        [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80],
        [0, 0x81, 0], // nonminimal length
        [2, 0x81, 0x80, 0x40], // declared payload over 1 MiB
        [4, 1], // close payload
        [6, 1], // reset payload
        [0x80], // truncated header
        [0, 0x80], // truncated length
        [0, 2, 1], // truncated stream name
        [0, 0, 2, 2, 1], // truncated stream payload
    ];

    [Test]
    [CancelAfter(10_000)]
    public async Task Malformed_frames_fail_the_connection_before_unbounded_allocation([ValueSource(nameof(MalformedFrames))] byte[] wire, CancellationToken token)
    {
        await using RawConnection peer = new();
        await peer.Transport.WriteAsync(new ReadOnlySequence<byte>(wire), token);
        await peer.Transport.WriteEofAsync(token);
        Assert.That(async () => await peer.Runner.WaitAsync(token), Throws.InstanceOf<PeerConnectionException>());
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Reset_during_a_partially_consumed_outgoing_frame_keeps_the_next_stream_framed(CancellationToken token)
    {
        await using RawConnection peer = new();
        peer.Requests.Add(new UpgradeOptions(), token);
        await peer.ReadFrameAsync(token);
        IChannel application = await peer.Opened.Reader.ReadAsync(token);
        byte[] payload = new byte[2 * MplexProtocol.OutboundChunkSize];
        Array.Fill(payload, (byte)0x3a);
        Task<IOResult> blockedWrite = application.WriteAsync(new ReadOnlySequence<byte>(payload), token).AsTask();
        (ulong id, byte flag, int size) = await peer.ReadHeaderAsync(token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((id, flag), Is.EqualTo((0UL, (byte)2)));
            Assert.That(size, Is.EqualTo(MplexProtocol.OutboundChunkSize));
        }
        await peer.Transport.ReadAsync(1, token: token).OrThrow();
        await peer.WriteFrameAsync(0, 5, [], token);
        await peer.WriteFrameAsync(1, 0, [], token);
        IChannel healthy = await peer.Opened.Reader.ReadAsync(token);
        await peer.WriteFrameAsync(1, 2, [0x55], token);
        Assert.That((await healthy.ReadAsync(1, token: token).OrThrow()).ToArray(), Is.EqualTo(new byte[] { 0x55 }));
        Assert.That((await peer.Transport.ReadAsync(size - 1, token: token).OrThrow()).ToArray(), Is.All.EqualTo((byte)0x3a));
        Assert.That(await blockedWrite, Is.Not.EqualTo(IOResult.Ok));
        Task<IOResult> reply = healthy.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 0x77 }), token).AsTask();
        Assert.That(await peer.ReadFrameAsync(token), Is.EqualTo((1UL, (byte)1, new byte[] { 0x77 })));
        await reply;
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Frame_deadline_aborts_and_joins_a_partially_consumed_write(CancellationToken token)
    {
        ManualTimeProvider time = new();
        await using RawConnection peer = new(time);
        await peer.WriteFrameAsync(0, 0, [], token);
        IChannel application = await peer.Opened.Reader.ReadAsync(token);
        Task<IOResult> writing = application.WriteAsync(new ReadOnlySequence<byte>(new byte[2 * MplexProtocol.OutboundChunkSize]), token).AsTask();
        (ulong id, byte flag, int size) = await peer.ReadHeaderAsync(token);
        Assert.That((id, flag, size), Is.EqualTo((0UL, (byte)1, MplexProtocol.OutboundChunkSize)));
        await peer.Transport.ReadAsync(1, token: token).OrThrow();

        await time.TimerCreated.WaitAsync(token);
        time.AdvanceAndFireTimer(TimeSpan.FromSeconds(10));
        await peer.Runner.WaitAsync(token);
        Assert.That(await writing.WaitAsync(token), Is.Not.EqualTo(IOResult.Ok));
        Assert.That((await peer.Transport.ReadAsync(1, token: token)).Result, Is.EqualTo(IOResult.Aborted));
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Connection_cancellation_unblocks_a_frame_writer_application_write_and_dial_dispatcher(CancellationToken token)
    {
        await using RawConnection peer = new();
        peer.Requests.Add(new UpgradeOptions(), token);
        IChannel application = await peer.Opened.Reader.ReadAsync(token);
        Task<IOResult> blocked = application.WriteAsync(new ReadOnlySequence<byte>(new byte[2 * MplexProtocol.OutboundChunkSize]), token).AsTask();
        await peer.Transport.AbortAsync();
        await peer.Runner.WaitAsync(token);
        Assert.That(await blocked, Is.Not.EqualTo(IOResult.Ok));
    }

    [Test]
    [CancelAfter(15_000)]
    public async Task Closed_streams_release_capacity_without_retaining_stream_id_history(CancellationToken token)
    {
        await using RawConnection peer = new();
        for (ulong id = 0; id < 2 * MplexProtocol.MaxStreams; id++)
        {
            await peer.WriteFrameAsync(id, 0, [], token);
            IChannel application = await peer.Opened.Reader.ReadAsync(token);
            await peer.WriteFrameAsync(id, 4, [], token);
            Assert.That((await application.ReadAsync(1, token: token)).Result, Is.EqualTo(IOResult.Ended));
            await application.WriteEofAsync(token);
            Assert.That(await peer.ReadFrameAsync(token), Is.EqualTo((id, (byte)3, Array.Empty<byte>())));
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Cleanup_joins_the_reset_owner_before_disposing_its_cancellation_source(CancellationToken token)
    {
        PausedAbortChannel channel = new();
        using StreamHarness stream = new(channel, token);

        // Direct invocation makes both cancelled pumps complete synchronously while the reset owner is paused, without a production test hook.
        Task reset = stream.ResetAsync();
        Task cleanup = stream.RunAsync();
        try
        {
            using IDisposable assertions = Assert.EnterMultipleScope();
            Assert.That(reset.IsCompleted, Is.False, "the reset still owns the cancellation source");
            Assert.That(cleanup.IsCompleted, Is.False, "cleanup must join the reset owner");
            Assert.That(channel.Aborts, Is.EqualTo(1), "cleanup cannot start a second shutdown while the first is pending");
            Assert.DoesNotThrow(() => _ = stream.Lifetime.Token, "the cancellation source must remain alive until its reset owner completes");
        }
        finally
        {
            channel.Release();
            await reset.WaitAsync(token);
            await cleanup.WaitAsync(token);
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Completed_application_drains_graceful_half_close_but_resets_an_aborted_half_close([Values] bool aborted, CancellationToken token)
    {
        CompletedApplicationChannel channel = new();
        using StreamHarness stream = new(channel, token);
        Task running = stream.RunAsync();
        channel.CompleteOutput(aborted);
        try
        {
            Task observed = await Task.WhenAny(channel.StateProbed, channel.ResetObserved).WaitAsync(token);
            if (aborted)
            {
                await channel.ResetObserved.WaitAsync(token);
                await running.WaitAsync(token);
                Assert.That(stream.Frames, Is.EqualTo(new byte[][] { [3, 0], [5, 0] }), "an abort after local FIN still resets the stream");
            }
            else
            {
                Assert.That(observed, Is.SameAs(channel.StateProbed), "clean application completion must preserve the response until the peer closes its direction");
                stream.CompleteInput();
                await running.WaitAsync(token);
                Assert.That(stream.Frames, Is.EqualTo(new byte[][] { [3, 0] }), "a FIN must not be followed by a reset that discards the peer's queued response");
            }
        }
        finally
        {
            stream.CompleteInput();
            await running.WaitAsync(token);
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Native_channel_probe_distinguishes_graceful_completion_from_abort_after_local_eof([Values] bool aborted, CancellationToken token)
    {
        Channel channel = new();
        IChannel application = channel.Reverse;
        await application.WriteEofAsync(token);
        Assert.That((await channel.ReadAsync(1, token: token)).Result, Is.EqualTo(IOResult.Ended));
        if (aborted) await application.AbortAsync();
        else await application.CloseAsync();
        Assert.That((await channel.ReadAsync(0, ReadBlockingMode.DoNotWait)).Result, Is.EqualTo(aborted ? IOResult.Aborted : IOResult.Ended));
    }

    private sealed class CompletedApplicationChannel : IChannel
    {
        private readonly TaskCompletionSource<ReadResult> _output = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stateProbed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resetObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _aborted;
        internal Task StateProbed => _stateProbed.Task;
        internal Task ResetObserved => _resetObserved.Task;
        internal void CompleteOutput(bool aborted)
        {
            _aborted = aborted;
            _closed.TrySetResult();
            _output.TrySetResult(ReadResult.Ended);
        }
        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            if (blockingMode != ReadBlockingMode.DoNotWait) return new(_output.Task);
            _stateProbed.TrySetResult();
            return ValueTask.FromResult(_aborted ? ReadResult.Aborted : ReadResult.Ended);
        }
        public ValueTask AbortAsync()
        {
            _aborted = true;
            _resetObserved.TrySetResult();
            _closed.TrySetResult();
            return ValueTask.CompletedTask;
        }
        public ValueTask CloseAsync() => ValueTask.CompletedTask;
        public TaskAwaiter GetAwaiter() => _closed.Task.GetAwaiter();
        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default) => ValueTask.FromResult(IOResult.Ok);
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => ValueTask.FromResult(IOResult.Ok);
    }

    private sealed class StreamHarness : IDisposable
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly Type ConnectionType = typeof(MplexProtocol).GetNestedType("Connection", BindingFlags.NonPublic)!;
        private static readonly Type StreamType = typeof(MplexProtocol).GetNestedType("MplexStream", BindingFlags.NonPublic)!;
        private static readonly Type KeyType = typeof(MplexProtocol).GetNestedType("StreamKey", BindingFlags.NonPublic)!;
        private readonly object _connection;
        private readonly object _stream;
        internal readonly ConcurrentQueue<byte[]> Frames = [];
        internal CancellationTokenSource Lifetime => (CancellationTokenSource)StreamType.GetField("_stop", Hidden)!.GetValue(_stream)!;

        internal StreamHarness(IChannel application, CancellationToken token)
        {
            IChannel transport = Substitute.For<IChannel>();
            transport.CancellationToken.Returns(token);
            transport.WriteAsync(Arg.Any<ReadOnlySequence<byte>>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Frames.Enqueue(call.ArgAt<ReadOnlySequence<byte>>(0).ToArray());
                return ValueTask.FromResult(IOResult.Ok);
            });
            _connection = ConnectionType.GetConstructors(Hidden).Single().Invoke([transport, Substitute.For<IConnectionContext>(), null, TimeProvider.System]);
            object key = Activator.CreateInstance(KeyType, 0UL, false)!;
            _stream = StreamType.GetConstructors(Hidden).Single().Invoke([_connection, key, application, token, null]);
        }
        internal Task ResetAsync() => (Task)StreamType.GetMethod("ResetAsync", Hidden)!.Invoke(_stream, [false])!;
        internal Task RunAsync() => (Task)StreamType.GetMethod("RunAsync", Hidden)!.Invoke(_stream, null)!;
        internal void CompleteInput()
        {
            StreamType.GetField("RemoteClosed", Hidden)!.SetValue(_stream, true);
            ((System.Threading.Channels.Channel<ReadOnlySequence<byte>>)StreamType.GetField("Input", Hidden)!.GetValue(_stream)!).Writer.TryComplete();
        }
        public void Dispose()
        {
            ((IDisposable)ConnectionType.GetField("_stop", Hidden)!.GetValue(_connection)!).Dispose();
            ((IDisposable)ConnectionType.GetField("_writer", Hidden)!.GetValue(_connection)!).Dispose();
        }
    }

    private sealed class PausedAbortChannel : IChannel
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _aborts;
        internal int Aborts => Volatile.Read(ref _aborts);
        internal void Release() => _release.TrySetResult();
        public ValueTask AbortAsync() => Interlocked.Increment(ref _aborts) == 1 ? new(_release.Task) : ValueTask.CompletedTask;
        public ValueTask CloseAsync() => ValueTask.CompletedTask;
        public TaskAwaiter GetAwaiter() => Task.CompletedTask.GetAwaiter();
        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default) =>
            ValueTask.FromResult(ReadResult.Cancelled);
        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default) => ValueTask.FromResult(IOResult.Ok);
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => ValueTask.FromResult(IOResult.Ok);
    }

    [Test]
    public void Production_stack_offers_yamux_before_mplex_as_alternatives([Values] bool quic)
    {
        using ServiceProvider services = new ServiceCollection().AddLibp2p(builder => quic ? builder.WithQuic() : builder).BuildServiceProvider();
        services.GetRequiredService<IPeerFactory>();
        MultiplexerSettings multiplexers = services.GetRequiredService<MultiplexerSettings>();
        Assert.That(multiplexers.Multiplexers.Select(protocol => protocol.Id), Is.EqualTo(new[] { "/yamux/1.0.0", "/mplex/6.7.0" }));
        IProtocolStackSettings stack = services.GetRequiredService<IProtocolStackSettings>();
        Assert.That(stack.Protocols!.Values.Any(protocols => protocols.Select(protocol => protocol.Protocol.Id)
            .SequenceEqual(new[] { "/yamux/1.0.0", "/mplex/6.7.0" })), Is.True);
        AssertMultistreamSelectors(services);
    }

    public enum ApplicationTransport
    {
        Yamux,
        Mplex,
        Quic,
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Unsupported_typed_request_fails_promptly_and_preserves_the_session([Values] ApplicationTransport transport, CancellationToken token)
    {
        if (transport == ApplicationTransport.Quic)
        {
            if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                Assert.That(QuicListener.IsSupported && QuicConnection.IsSupported, Is.True, "Windows CI must exercise QUIC with the bundled MsQuic runtime.");
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported) Assert.Ignore("MsQuic is unavailable on this runner.");
        }
        await using ServiceProvider clientServices = new ServiceCollection().AddLibp2p(builder =>
            (transport == ApplicationTransport.Quic ? builder.WithQuic() : builder)
                .AddProtocol<TypedRequestProtocol>().AddProtocol<UnsupportedTypedProtocol>()).BuildServiceProvider();
        await using ServiceProvider serverServices = new ServiceCollection().AddLibp2p(builder =>
            (transport == ApplicationTransport.Mplex ? (IPeerFactoryBuilder)new MplexOnlyBuilder(builder.ServiceProvider)
                : transport == ApplicationTransport.Quic ? builder.WithQuic() : builder)
                .AddProtocol<TypedRequestProtocol>()).BuildServiceProvider();
        await using ILocalPeer client = clientServices.GetRequiredService<IPeerFactory>().Create();
        await using ILocalPeer server = serverServices.GetRequiredService<IPeerFactory>().Create();
        AssertMultistreamSelectors(clientServices);
        AssertMultistreamSelectors(serverServices);
        string address = transport == ApplicationTransport.Quic
            ? $"/ip4/127.0.0.1/udp/{PeerSessionNodes.FreeUdpPort()}/quic-v1" : "/ip4/127.0.0.1/tcp/0";
        await server.StartListenAsync([Multiaddress.Decode(address)], token);
        ISession session = await client.DialAsync(server.ListenAddresses.Single(), token);
        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(token);
        request.CancelAfter(TimeSpan.FromSeconds(5));
        Task<ulong> refused = session.DialAsync<UnsupportedTypedProtocol, ulong, ulong>(7, request.Token);
        try
        {
            Assert.That(async () => await refused.WaitAsync(TimeSpan.FromSeconds(2), token), Throws.TypeOf<ChannelClosedException>());
            Assert.That(request.IsCancellationRequested, Is.False, "protocol rejection must complete before the caller's deadline");
        }
        finally
        {
            request.Cancel();
            await ((Task)refused).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        Assert.That(await session.DialAsync<TypedRequestProtocol, ulong, ulong>(42, token), Is.EqualTo(42UL));
        await session.DialAsync<PingProtocol>(token);
    }

    private static void AssertMultistreamSelectors(ServiceProvider services)
    {
        IProtocolStackSettings stack = services.GetRequiredService<IProtocolStackSettings>();
        IProtocol[] selectors = stack.Protocols!.Keys.Concat(stack.Protocols.Values.SelectMany(static protocols => protocols))
            .Where(static protocol => protocol.Protocol.Id == "/multistream/1.0.0").Select(static protocol => protocol.Protocol).ToArray();
        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(selectors, Is.Not.Empty);
        Assert.That(selectors.Select(static selector => selector.GetType()), Is.All.EqualTo(typeof(MultistreamProtocol)));
        Assert.That(typeof(MultistreamProtocol).Assembly, Is.SameAs(typeof(Libp2pStackBuilder).Assembly));
    }

    private sealed class TypedRequestProtocol : ISessionProtocol<ulong, ulong>
    {
        public string Id => "/test/multistream/typed/1";
        public async Task<ulong> DialAsync(IChannel channel, ISessionContext context, ulong request)
        {
            await channel.WriteAsync(new ReadOnlySequence<byte>(new[] { checked((byte)request) })).OrThrow();
            return (await channel.ReadAsync(1).OrThrow()).ToArray()[0];
        }
        public async Task ListenAsync(IChannel channel, ISessionContext context)
        {
            ReadOnlySequence<byte> request = await channel.ReadAsync(1).OrThrow();
            await channel.WriteAsync(request).OrThrow();
        }
    }

    private sealed class UnsupportedTypedProtocol : ISessionProtocol<ulong, ulong>
    {
        public string Id => "/test/multistream/unsupported/1";
        public Task<ulong> DialAsync(IChannel channel, ISessionContext context, ulong request) => throw new InvalidOperationException("A rejected protocol must not dispatch its handler.");
        public Task ListenAsync(IChannel channel, ISessionContext context) => throw new InvalidOperationException("The remote does not register this protocol.");
    }

    [Test]
    [CancelAfter(20_000)]
    public async Task Production_tcp_stack_negotiates_with_a_mplex_only_peer_and_exchanges_ping([Values] bool productionDials, CancellationToken token)
    {
        await using ServiceProvider productionServices = new ServiceCollection().AddLibp2p().BuildServiceProvider();
        await using ServiceProvider mplexServices = new ServiceCollection().AddLibp2p(builder => new MplexOnlyBuilder(builder.ServiceProvider)).BuildServiceProvider();
        await using ILocalPeer production = productionServices.GetRequiredService<IPeerFactory>().Create();
        await using ILocalPeer mplex = mplexServices.GetRequiredService<IPeerFactory>().Create();
        ILocalPeer listener = productionDials ? mplex : production;
        ILocalPeer dialer = productionDials ? production : mplex;
        await listener.StartListenAsync([Multiaddress.Decode("/ip4/127.0.0.1/tcp/0")], token);
        ISession session = await dialer.DialAsync(listener.ListenAddresses.Single(), token);
        await session.DialAsync<PingProtocol>(token);
    }

    [Test]
    [CancelAfter(20_000)]
    public async Task Cancelling_an_outgoing_application_request_resets_its_remote_stream_and_keeps_the_session_usable(CancellationToken token)
    {
        HoldProtocol held = new();
        await using ServiceProvider productionServices = new ServiceCollection().AddLibp2p(builder => builder.AddProtocol<HoldProtocol>()).BuildServiceProvider();
        await using ServiceProvider mplexServices = new ServiceCollection().AddLibp2p(builder => new MplexOnlyBuilder(builder.ServiceProvider)
            .AddProtocol(held)).BuildServiceProvider();
        await using ILocalPeer production = productionServices.GetRequiredService<IPeerFactory>().Create();
        await using ILocalPeer mplex = mplexServices.GetRequiredService<IPeerFactory>().Create();
        await mplex.StartListenAsync([Multiaddress.Decode("/ip4/127.0.0.1/tcp/0")], token);
        ISession session = await production.DialAsync(mplex.ListenAddresses.Single(), token);
        for (int index = 0; index < 3; index++)
        {
            using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task pending = session.DialAsync<HoldProtocol>(request.Token);
            IChannel remote = await held.Started.Reader.ReadAsync(token);
            request.Cancel();
            Assert.That(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
            await WaitClosedAsync(remote).WaitAsync(token);
            Assert.That((await remote.ReadAsync(1, token: token)).Result, Is.EqualTo(IOResult.Aborted));
        }
        await session.DialAsync<PingProtocol>(token);
    }

    private static async Task WaitClosedAsync(IChannel channel) => await channel;

    private sealed class HoldProtocol : ISessionProtocol
    {
        internal readonly System.Threading.Channels.Channel<IChannel> Started = System.Threading.Channels.Channel.CreateUnbounded<IChannel>();
        public string Id => "/test/mplex/cancellation/1";
        public async Task DialAsync(IChannel downChannel, ISessionContext context) => await downChannel.ReadAsync(1).OrThrow();
        public async Task ListenAsync(IChannel downChannel, ISessionContext context)
        {
            Started.Writer.TryWrite(downChannel);
            await downChannel;
        }
    }

    private sealed class MplexOnlyBuilder(IServiceProvider provider) : PeerFactoryBuilderBase<MplexOnlyBuilder, Libp2pStackPeerFactory>(provider)
    {
        protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
        {
            ProtocolRef tcp = Get<IpTcpProtocol>();
            Connect([tcp], [Get<MultistreamProtocol>()], [Get<NoiseProtocol>()], [Get<MultistreamProtocol>()], [Get<MplexProtocol>()],
                [Get<MultistreamProtocol>()], [Get<IdentifyProtocol>(), Get<IdentifyPushProtocol>(), Get<PingProtocol>(), .. additionalProtocols]);
            return [tcp];
        }
    }

    private sealed class RawConnection : IAsyncDisposable
    {
        private readonly Channel _connection = new();
        private readonly CancellationTokenSource _closed = new();
        internal readonly BlockingCollection<UpgradeOptions> Requests = [];
        internal readonly System.Threading.Channels.Channel<IChannel> Opened = System.Threading.Channels.Channel.CreateUnbounded<IChannel>();
        internal IChannel Transport => _connection.Reverse;
        internal Task Runner { get; }

        internal RawConnection(TimeProvider? timeProvider = null)
        {
            IConnectionContext context = Substitute.For<IConnectionContext>();
            INewSessionContext session = Substitute.For<INewSessionContext>();
            session.Token.Returns(_closed.Token);
            session.DialRequests.Returns(Requests.GetConsumingEnumerable(_closed.Token));
            context.UpgradeToSession().Returns(session);
            context.DisconnectAsync().Returns(_ => { _closed.Cancel(); return Task.CompletedTask; });
            session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
            {
                Channel channel = new();
                Opened.Writer.TryWrite(channel.Reverse);
                return channel;
            });
            Runner = new MplexProtocol(timeProvider: timeProvider).DialAsync(_connection, context);
        }

        internal async Task WriteFrameAsync(ulong id, byte flag, byte[] bytes, CancellationToken token)
        {
            using MemoryStream wire = new();
            WriteVarint(wire, (id << 3) | flag);
            WriteVarint(wire, (ulong)bytes.Length);
            wire.Write(bytes);
            await Transport.WriteAsync(new ReadOnlySequence<byte>(wire.ToArray()), token).OrThrow();
        }

        internal async Task<(ulong Id, byte Flag, byte[] Data)> ReadFrameAsync(CancellationToken token)
        {
            (ulong id, byte flag, int size) = await ReadHeaderAsync(token);
            byte[] data = size == 0 ? [] : (await Transport.ReadAsync(size, token: token).OrThrow()).ToArray();
            return (id, flag, data);
        }

        internal async Task<(ulong Id, byte Flag, int Size)> ReadHeaderAsync(CancellationToken token)
        {
            ulong header = await ReadVarintAsync(token);
            return (header >> 3, (byte)(header & 7), (int)await ReadVarintAsync(token));
        }

        private async Task<ulong> ReadVarintAsync(CancellationToken token)
        {
            ulong value = 0;
            for (int shift = 0; shift < 63; shift += 7)
            {
                byte current = (await Transport.ReadAsync(1, token: token).OrThrow()).FirstSpan[0];
                value |= (ulong)(current & 127) << shift;
                if ((current & 128) == 0) return value;
            }
            throw new InvalidDataException("Invalid peer test frame");
        }

        private static void WriteVarint(Stream wire, ulong value)
        {
            while (value >= 128)
            {
                wire.WriteByte((byte)((value & 127) | 128));
                value >>= 7;
            }
            wire.WriteByte((byte)value);
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.AbortAsync();
            _closed.Cancel();
            await Runner.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Requests.Dispose();
            _closed.Dispose();
        }
    }
}
