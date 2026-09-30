// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiformats.Address;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P.ReqResp;

/// <summary>A request is served without waiting for the requester's EOF, so the stream is watched beside serving: what it sees is reported, never allowed to disturb the response.</summary>
public class InboundRequestWatchTests
{
    private const string ProbeId = "/test/inbound-request-watch/1";
    private const string ByRangeId = "/eth2/beacon_chain/req/beacon_blocks_by_range/2/ssz_snappy";

    private static readonly PeerId Requester = new Identity(privateKey: null, KeyType.Secp256K1).PeerId;

    // A requester can send what it must not once the response is out, and the channel is torn down as soon as the listener returns.
    [Test]
    [CancelAfter(30_000)]
    public async Task The_listener_returns_only_once_the_requester_ended_its_stream_reporting_nothing(CancellationToken token)
    {
        List<string> reported = [];
        using LateByteStream stream = new(await RequestBytesAsync(token), lateByte: null);
        ProbeProtocol protocol = new((_, detail) => reported.Add(detail));
        protocol.Enter(Context());

        await protocol.ReadAsync(stream, token);
        await stream.WatchStarted.WaitAsync(token);
        Task listenerReturn = protocol.DisposeAsync().AsTask();
        bool returnedBeforeTheRequesterEnded = listenerReturn.IsCompleted;
        stream.Teardown();
        await listenerReturn.WaitAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(returnedBeforeTheRequesterEnded, Is.False, "the channel is torn down when the listener returns, so the listener waits for the requester");
            Assert.That(reported, Is.Empty, "a requester that ends its stream is not a violation");
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_byte_sent_after_the_response_is_complete_is_reported_against_the_peer(CancellationToken token)
    {
        TaskCompletionSource<PeerId> reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using LateByteStream stream = new(await RequestBytesAsync(token), lateByte: null);
        ProbeProtocol protocol = new((peer, _) => reported.TrySetResult(peer));
        protocol.Enter(Context());
        await protocol.ReadAsync(stream, token);
        await stream.WatchStarted.WaitAsync(token);
        Task listenerReturn = protocol.DisposeAsync().AsTask();

        stream.SendLate(0x2a);

        Assert.That(await reported.Task.WaitAsync(token), Is.EqualTo(Requester));
        await listenerReturn.WaitAsync(token);
    }

    // A requester that never ends its stream must not hold a listener for good.
    [Test]
    [CancelAfter(30_000)]
    public async Task The_listener_returns_after_the_linger_when_the_requester_never_ends_its_stream(CancellationToken token)
    {
        List<string> reported = [];
        using LateByteStream stream = new(await RequestBytesAsync(token), lateByte: null);
        ProbeProtocol protocol = new((_, detail) => reported.Add(detail)) { WatchLingerAfterServed = TimeSpan.FromMilliseconds(100) };
        protocol.Enter(Context());
        await protocol.ReadAsync(stream, token);
        await stream.WatchStarted.WaitAsync(token);

        await protocol.DisposeAsync().AsTask().WaitAsync(token);

        Assert.That(reported, Is.Empty, "the end of the linger is not a violation");
    }

    // A peer that holds its streams open must not hold more of them than the cap allows.
    [Test]
    [CancelAfter(30_000)]
    public async Task A_stream_held_for_the_requester_to_end_keeps_the_peers_concurrency_slot(CancellationToken token)
    {
        using LateByteStream stream = new(await RequestBytesAsync(token), lateByte: null);
        ProbeProtocol protocol = new((_, _) => { });
        ISessionContext context = Context();
        protocol.Enter(context);
        await protocol.ReadAsync(stream, token);
        await stream.WatchStarted.WaitAsync(token);
        Task lingering = protocol.DisposeAsync().AsTask();
        await using IAsyncDisposable? second = protocol.TryEnterAnother(context);

        IAsyncDisposable? third = protocol.TryEnterAnother(context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.Not.Null, "test setup: one slot is free beside the lingering stream");
            Assert.That(third, Is.Null, "the lingering stream still counts against the peer");
        }

        stream.Teardown();
        await lingering.WaitAsync(token);
    }

    // The linger ends a watch only if a pending channel read honours its token.
    [Test]
    public void A_pending_read_on_a_libp2p_channel_ends_when_its_token_is_cancelled()
    {
        using ChannelStreamAdapter stream = new(new Channel());
        using CancellationTokenSource served = new();
        Task<int> read = stream.ReadAsync(new byte[1], served.Token).AsTask();

        served.Cancel();

        Assert.CatchAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_late_byte_is_recorded_as_an_invalid_message_and_handed_to_the_sink_with_the_peer(CancellationToken token)
    {
        TaskCompletionSource<(PeerId, string)> reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using LateByteStream stream = new(await RequestBytesAsync(token), lateByte: 0x2a);
        ProbeProtocol protocol = new((peer, detail) => reported.TrySetResult((peer, detail)));
        long before = InvalidMessageCount();
        protocol.Enter(Context());
        await using ProbeProtocol probe = protocol;

        await protocol.ReadAsync(stream, token);
        (PeerId peer, string detail) = await reported.Task.WaitAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer, Is.EqualTo(Requester));
            Assert.That(detail, Does.StartWith(ProbeId));
            Assert.That(InvalidMessageCount(), Is.EqualTo(before + 1), "the failure metric counts the violation");
        }
    }

    // A sink that fails (a pool resolved after shutdown, say) must not surface as an unobserved task exception.
    [Test]
    [CancelAfter(30_000)]
    public async Task A_sink_that_throws_leaves_no_unobserved_task_exception(CancellationToken token)
    {
        const string message = "sink failure of the inbound request watch test";
        TaskCompletionSource sinkCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool unobserved = false;
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs e) =>
            unobserved |= e.Exception.Flatten().InnerExceptions.Any(static x => x.Message == message);

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            using (LateByteStream stream = new(await RequestBytesAsync(token), lateByte: 0x2a))
            {
                ProbeProtocol protocol = new((_, _) =>
                {
                    sinkCalled.TrySetResult();
                    throw new InvalidOperationException(message);
                });
                protocol.Enter(Context());
                await using ProbeProtocol probe = protocol;
                await protocol.ReadAsync(stream, token);
                await sinkCalled.Task.WaitAsync(token);
            }

            await Task.Delay(100, token);
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        Assert.That(unobserved, Is.False);
    }

    // Bytes that arrive once the response has begun are reported and the requester still gets every chunk.
    [Test]
    [CancelAfter(60_000)]
    public async Task Bytes_sent_between_response_chunks_are_reported_and_every_chunk_still_arrives(CancellationToken token)
    {
        BeaconChainSpec spec = BeaconChainSpec.Mainnet;
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), spec);
        (SignedBeaconBlock anchor, Hash256 anchorRoot, SignedBeaconBlock[] blocks) = TestChain.BuildLinkedChain(0, 1, 2, 3);
        TestChain.Persist(store, anchor, anchorRoot, blocks);
        TaskCompletionSource<string> reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BeaconBlocksByRangeProtocolV2 protocol = new(spec, store) { RequestViolationSink = (_, detail) => reported.TrySetResult(detail) };

        Channel channel = new();
        TaskCompletionSource holdEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IChannel serverSide = HoldSecondWrite(channel.Reverse, holdEntered, hold);
        Task listen = Task.Run(async () =>
        {
            await protocol.ListenAsync(serverSide, Context());
            await channel.Reverse.WriteEofAsync();
        }, token);

        ChannelStreamAdapter client = new(channel);
        await ReqRespFraming.WriteRequestAsync(client, BeaconBlocksByRangeRequest.Encode(new BeaconBlocksByRangeRequest { StartSlot = 1, Count = 3, Step = 1 }), token);
        List<ResponseChunk> chunks = [];
        chunks.Add((await ReqRespFraming.ReadResponseChunkAsync(client, contextBytesLength: 4, ReqRespFraming.MaxPayloadSize, token))!.Value);
        await holdEntered.Task.WaitAsync(token);
        await client.WriteAsync(new byte[] { 0x2a }, token);
        string detail = await reported.Task.WaitAsync(token);
        hold.SetResult();

        while (await ReqRespFraming.ReadResponseChunkAsync(client, contextBytesLength: 4, ReqRespFraming.MaxPayloadSize, token) is { } chunk)
        {
            chunks.Add(chunk);
        }

        await channel.WriteEofAsync(token);
        await listen;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(detail, Does.Contain("Unexpected bytes"));
            Assert.That(chunks, Has.Count.EqualTo(3));
            Assert.That(chunks, Has.All.Matches<ResponseChunk>(static c => c.Result == ReqRespFraming.ResponseCode.Success));
        }
    }

    private static long InvalidMessageCount() =>
        Metrics.BeaconChainReqRespFailures.TryGetValue(new ReqRespFailureKey(ProbeId, ReqRespFailureReason.InvalidMessage), out long count) ? count : 0;

    private static ISessionContext Context()
    {
        ISessionContext context = Substitute.For<ISessionContext>();
        Nethermind.Libp2p.Core.State state = new();
        state.RemoteAddress = Multiaddress.Decode($"/ip4/127.0.0.1/tcp/4001/p2p/{Requester}");
        context.State.Returns(state);
        return context;
    }

    private static async Task<byte[]> RequestBytesAsync(CancellationToken token)
    {
        using MemoryStream wire = new();
        await ReqRespFraming.WriteRequestAsync(wire, new byte[8], token);
        return wire.ToArray();
    }

    /// <summary>Passes everything through except that the second write blocks until released; an in-memory write completes only once read, so the listener then sits between two chunks.</summary>
    private static IChannel HoldSecondWrite(IChannel inner, TaskCompletionSource entered, TaskCompletionSource release)
    {
        int writes = 0;
        IChannel held = Substitute.For<IChannel>();
        held.ReadAsync(Arg.Any<int>(), Arg.Any<ReadBlockingMode>(), Arg.Any<CancellationToken>())
            .Returns(call => inner.ReadAsync(call.ArgAt<int>(0), call.ArgAt<ReadBlockingMode>(1), call.ArgAt<CancellationToken>(2)));
        held.WriteAsync(Arg.Any<ReadOnlySequence<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => HeldWriteAsync(call.Arg<ReadOnlySequence<byte>>(), call.Arg<CancellationToken>()));
        held.WriteEofAsync(Arg.Any<CancellationToken>()).Returns(call => inner.WriteEofAsync(call.Arg<CancellationToken>()));
        return held;

        async ValueTask<IOResult> HeldWriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token)
        {
            if (Interlocked.Increment(ref writes) == 2)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }

            return await inner.WriteAsync(bytes, token);
        }
    }

    /// <summary>Exposes the protected slot of <see cref="ReqRespProtocolBase"/> to the tests.</summary>
    private sealed class ProbeProtocol : ReqRespProtocolBase, IAsyncDisposable
    {
        public ProbeProtocol(Action<PeerId, string> sink) => RequestViolationSink = sink;

        private InboundRequest? _request;

        public void Enter(ISessionContext context) => _request = TryEnterInbound(context, ProbeId);

        public Task<byte[]> ReadAsync(Stream stream, CancellationToken token) => _request!.ReadRequestAsync(stream, maxSize: 8, token);

        public IAsyncDisposable? TryEnterAnother(ISessionContext context) => TryEnterInbound(context, ProbeId);

        public ValueTask DisposeAsync() => _request!.DisposeAsync();
    }

    /// <summary>Serves the request, then answers the first read as "nothing buffered", then blocks on the next until cancelled, torn down, or handed one late byte.</summary>
    private sealed class LateByteStream : Stream
    {
        private readonly byte[] _request;
        private readonly TaskCompletionSource<byte?> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _watchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _watchEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;
        private int _readsAfterRequest;

        public LateByteStream(byte[] request, byte? lateByte)
        {
            _request = request;
            if (lateByte is not null)
            {
                _next.TrySetResult(lateByte);
            }
        }

        public Task WatchStarted => _watchStarted.Task;

        public Task WatchEnded => _watchEnded.Task;

        /// <summary>Ends the stream the way a torn-down channel does: the pending read returns no bytes.</summary>
        public void Teardown() => _next.TrySetResult(null);

        public void SendLate(byte late) => _next.TrySetResult(late);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < _request.Length)
            {
                int count = Math.Min(buffer.Length, _request.Length - _position);
                _request.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            if (_readsAfterRequest++ == 0)
            {
                return 0;
            }

            _watchStarted.TrySetResult();
            try
            {
                if (await _next.Task.WaitAsync(cancellationToken) is not { } late)
                {
                    _watchEnded.TrySetResult();
                    return 0;
                }

                buffer.Span[0] = late;
                return 1;
            }
            catch (OperationCanceledException)
            {
                _watchEnded.TrySetResult();
                throw;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
