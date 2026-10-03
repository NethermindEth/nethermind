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
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
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

    // Completed responses release capacity while closure is watched (consensus-specs networking, Req/Resp interaction).
    [Test]
    [CancelAfter(30_000)]
    public async Task A_completed_response_releases_the_peers_concurrency_slot([Values] bool unattributed, CancellationToken token)
    {
        using LateByteStream wire = new(await RequestBytesAsync(token), lateByte: null);
        List<string> reported = [];
        ProbeProtocol protocol = new((_, detail) => reported.Add(detail));
        ISessionContext context = unattributed ? ContextWithoutPeer() : Context();
        protocol.Enter(context);
        await protocol.ReadAsync(wire, token);
        await wire.WatchStarted.WaitAsync(token);
        Task lingering = protocol.DisposeAsync().AsTask();
        await using IAsyncDisposable? second = protocol.TryEnterAnother(context);
        await using IAsyncDisposable? third = protocol.TryEnterAnother(context);

        await using IAsyncDisposable? fourth = protocol.TryEnterAnother(context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lingering.IsCompleted, Is.False, "test setup: the listener is still waiting for the requester");
            Assert.That(second, Is.Not.Null, "the completed response no longer counts against the peer");
            Assert.That(third, Is.Not.Null);
            Assert.That(fourth, Is.Null, "the cap still bounds requests being served");
        }

        wire.SendLate(0x2a);
        await lingering.WaitAsync(token);
        Assert.That(reported, Has.Count.EqualTo(unattributed ? 0 : 1), "late bytes are still watched after capacity is released");
    }

    // Bound listeners awaiting requester closure (consensus-specs networking, Req/Resp interaction).
    [Test]
    [CancelAfter(30_000)]
    public async Task Completed_responses_awaiting_requester_closure_are_bounded_while_further_requests_are_served([Values] bool unattributed, CancellationToken token)
    {
        ProbeProtocol protocol = new((_, _) => { });
        ISessionContext context = unattributed ? ContextWithoutPeer() : Context();
        for (int iteration = 0; iteration < 2; iteration++)
        {
            List<LateByteStream> wires = [];
            List<Task> lingering = [];
            for (int i = 0; i < ProbeProtocol.LingerBudget + 4; i++)
            {
                LateByteStream wire = new(await RequestBytesAsync(token), lateByte: null);
                wires.Add(wire);
                IAsyncDisposable? request = await protocol.ServeAsync(context, wire, token);
                Assert.That(request, Is.Not.Null, "a peer withholding closure is still served");
                await wire.WatchStarted.WaitAsync(token);
                lingering.Add(request!.DisposeAsync().AsTask());
            }

            Task overBudget = Task.WhenAll(lingering.Skip(ProbeProtocol.LingerBudget));
            Assert.DoesNotThrowAsync(() => overBudget.WaitAsync(TimeSpan.FromSeconds(5), token), "a listener over the budget returns at once");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(lingering.Take(ProbeProtocol.LingerBudget).Count(static t => !t.IsCompleted), Is.EqualTo(ProbeProtocol.LingerBudget), "the listeners within the budget still wait for the requester");
                Assert.That(wires.Skip(ProbeProtocol.LingerBudget).All(static s => s.WatchEnded.IsCompleted), Is.True);
            }

            wires.ForEach(static s => s.Teardown());
            await Task.WhenAll(lingering).WaitAsync(token);

            IAsyncDisposable? afterwards = protocol.TryEnterAnother(context);
            Assert.That(afterwards, Is.Not.Null);
            await afterwards!.DisposeAsync();
        }
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

    [Test]
    [CancelAfter(60_000)]
    public async Task Range_reply_stops_when_a_reorg_replaces_its_served_ancestors([Values] bool columns, [Values] bool belowAnchor, CancellationToken token)
    {
        const ulong start = 13_410_304;
        BeaconChainSpec spec = BeaconChainSpec.Mainnet;
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), spec);
        (SignedBeaconBlock anchor, Hash256 anchorRoot, SignedBeaconBlock[] blocks) = TestChain.BuildLinkedChain(start - 1, start, start + 1, start + 2, start + 3);
        TestChain.Persist(store, anchor, anchorRoot, blocks);
        if (belowAnchor)
        {
            store.SetAnchor(SszRoots.HashTreeRoot(blocks[^1].Message!), start + 3);
            store.BackfilledBlockFloor = start;
        }

        DataColumnSidecarPool pool = new();
        foreach (SignedBeaconBlock block in blocks)
        {
            pool.Add(SszRoots.HashTreeRoot(block.Message!), block.Message!.Slot,
                DataColumnSidecarTestFixture.BuildValidSidecar(0, block.Message.Slot, blobCount: 1));
        }

        Channel channel = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IChannel server = HoldSecondWrite(channel.Reverse, entered, release);
        Task listening = Task.Run(async () =>
        {
            try
            {
                if (columns) await new DataColumnSidecarsByRangeProtocol(spec, pool, store).ListenAsync(server, Context());
                else await new BeaconBlocksByRangeProtocolV2(spec, store).ListenAsync(server, Context());
            }
            finally
            {
                await channel.Reverse.WriteEofAsync(token);
            }
        }, token);
        ChannelStreamAdapter input = new(channel);
        byte[] request = columns
            ? DataColumnSidecarsByRangeRequest.Encode(new DataColumnSidecarsByRangeRequest { StartSlot = start, Count = 3, Columns = [0] })
            : BeaconBlocksByRangeRequest.Encode(new BeaconBlocksByRangeRequest { StartSlot = start, Count = 3, Step = 1 });
        await ReqRespFraming.WriteRequestAsync(input, request, token);
        await channel.WriteEofAsync(token);
        List<ResponseChunk> received = [(await ReqRespFraming.ReadResponseChunkAsync(input, 4, ReqRespFraming.MaxPayloadSize, token))!.Value];
        await entered.Task.WaitAsync(token);
        SignedBeaconBlock replacement = TestChain.CreateBlock(start + 1, anchorRoot);
        Hash256 replacementRoot = SszRoots.HashTreeRoot(replacement.Message!);
        SignedBeaconBlock next = TestChain.CreateBlock(start + 2, replacementRoot);
        Hash256 nextRoot = SszRoots.HashTreeRoot(next.Message!);
        store.PutBlock(replacementRoot, replacement);
        store.PutBlock(nextRoot, next);
        pool.Add(nextRoot, start + 2, DataColumnSidecarTestFixture.BuildValidSidecar(0, start + 2, blobCount: 1));
        store.ApplyCanonicalIndexChanges([(start, null), (start + 1, replacementRoot), (start + 2, nextRoot)], start + 2);
        release.SetResult();
        while (await ReqRespFraming.ReadResponseChunkAsync(input, 4, ReqRespFraming.MaxPayloadSize, token) is { } chunk)
        {
            received.Add(chunk);
        }

        await listening.WaitAsync(token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(received, Has.Count.EqualTo(2));
            Assert.That(received, Has.All.Matches<ResponseChunk>(static c => c.Result == ReqRespFraming.ResponseCode.Success));
        }
    }

    // p2p-interface.md range replies: reread passed slots to keep the reply on one chain.
    [Test]
    [CancelAfter(60_000)]
    public async Task Range_reply_stops_when_a_reorg_rewrites_a_slot_it_already_passed([Values] bool columns, [Values] bool passedSlotWasEmpty, CancellationToken token)
    {
        const ulong start = 13_410_304;
        BeaconChainSpec spec = BeaconChainSpec.Mainnet;
        SlotReadHookColumnsDb db = new(start + 2);
        BeaconChainStore store = new(db, spec);
        (SignedBeaconBlock anchor, Hash256 anchorRoot, SignedBeaconBlock[] blocks) =
            TestChain.BuildLinkedChain(start - 1, passedSlotWasEmpty ? [start, start + 2] : [start, start + 1, start + 2]);
        TestChain.Persist(store, anchor, anchorRoot, blocks);
        Hash256 firstRoot = SszRoots.HashTreeRoot(blocks[0].Message!);
        SignedBeaconBlock replacement = TestChain.CreateBlock(start + 1, firstRoot);
        replacement.Message!.ProposerIndex = 22;
        Hash256 replacementRoot = SszRoots.HashTreeRoot(replacement.Message);
        SignedBeaconBlock next = TestChain.CreateBlock(start + 2, replacementRoot);
        Hash256 nextRoot = SszRoots.HashTreeRoot(next.Message!);
        store.PutBlock(replacementRoot, replacement);
        store.PutBlock(nextRoot, next);
        DataColumnSidecarPool pool = new();
        pool.Add(firstRoot, start, DataColumnSidecarTestFixture.BuildValidSidecar(0, start, blobCount: 1));
        pool.Add(nextRoot, start + 2, DataColumnSidecarTestFixture.BuildValidSidecar(0, start + 2, blobCount: 1));
        db.OnRead = () => store.ApplyCanonicalIndexChanges([(start + 1, replacementRoot), (start + 2, nextRoot)], start + 2);

        Channel channel = new();
        Task listening = Task.Run(async () =>
        {
            try
            {
                if (columns) await new DataColumnSidecarsByRangeProtocol(spec, pool, store).ListenAsync(channel.Reverse, Context());
                else await new BeaconBlocksByRangeProtocolV2(spec, store).ListenAsync(channel.Reverse, Context());
            }
            finally
            {
                await channel.Reverse.WriteEofAsync(token);
            }
        }, token);
        ChannelStreamAdapter input = new(channel);
        byte[] request = columns
            ? DataColumnSidecarsByRangeRequest.Encode(new DataColumnSidecarsByRangeRequest { StartSlot = start, Count = 3, Columns = [0] })
            : BeaconBlocksByRangeRequest.Encode(new BeaconBlocksByRangeRequest { StartSlot = start, Count = 3, Step = 1 });
        await ReqRespFraming.WriteRequestAsync(input, request, token);
        await channel.WriteEofAsync(token);
        List<ResponseChunk> received = [];
        while (await ReqRespFraming.ReadResponseChunkAsync(input, 4, ReqRespFraming.MaxPayloadSize, token) is { } chunk)
        {
            received.Add(chunk);
        }

        await listening.WaitAsync(token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(received, Has.Count.EqualTo(columns || passedSlotWasEmpty ? 1 : 2), "nothing of the new chain follows what was served of the old one");
            Assert.That(received, Has.All.Matches<ResponseChunk>(static c => c.Result == ReqRespFraming.ResponseCode.Success));
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Column_reply_caps_sidecars_at_a_whole_block_after_empty_slots([Values(2, 130)] int blockCount, [Values] bool atLastSlot, CancellationToken token)
    {
        BeaconChainSpec spec = EnvelopeChain.Spec;
        ulong start = atLastSlot ? ulong.MaxValue - (ulong)blockCount - 1 : spec.GloasForkEpoch * spec.SlotsPerEpoch;
        ulong[] columns = [.. Enumerable.Range(0, 127).Select(static c => (ulong)c)];
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), spec);
        DataColumnSidecarPool pool = new(capacity: 130 * columns.Length);
        for (ulong offset = 2; offset < (ulong)blockCount + 2; offset++)
        {
            Hash256 root = Keccak.Compute($"columns {offset}");
            store.SetCanonicalRoot(start + offset, root);
            foreach (ulong column in columns)
            {
                pool.AddGloas(new DataColumnSidecarGloas { Slot = start + offset, BeaconBlockRoot = root, Index = column, Column = [], KzgProofs = [] });
            }
        }

        Channel channel = new();
        Task listening = Task.Run(async () =>
        {
            try
            {
                await new DataColumnSidecarsByRangeProtocol(spec, pool, store).ListenAsync(channel.Reverse, Context());
            }
            finally
            {
                await channel.Reverse.WriteEofAsync(token);
            }
        }, token);
        ChannelStreamAdapter input = new(channel);
        await ReqRespFraming.WriteRequestAsync(input, DataColumnSidecarsByRangeRequest.Encode(
            new DataColumnSidecarsByRangeRequest { StartSlot = start, Count = (ulong)blockCount + 2, Columns = columns }), token);
        await channel.WriteEofAsync(token);
        List<(ulong Slot, ulong Column)> received = [];
        while (await ReqRespFraming.ReadResponseChunkAsync(input, 4, ReqRespFraming.MaxPayloadSize, token) is { } chunk)
        {
            Assert.That(chunk.Result, Is.EqualTo(ReqRespFraming.ResponseCode.Success));
            DataColumnSidecarGloas.Decode(chunk.Payload, out DataColumnSidecarGloas sidecar);
            received.Add((sidecar.Slot, sidecar.Index));
        }

        await listening.WaitAsync(token);
        Assert.That(received, Is.EqualTo(Enumerable.Range(2, Math.Min(blockCount, 129)).SelectMany(offset => columns.Select(column => (start + (ulong)offset, column)))));
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

    private static ISessionContext ContextWithoutPeer()
    {
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new Nethermind.Libp2p.Core.State());
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

        public const int LingerBudget = MaxLingeringRequests;

        public async Task<IAsyncDisposable?> ServeAsync(ISessionContext context, Stream wire, CancellationToken token)
        {
            InboundRequest? request = TryEnterInbound(context, ProbeId);
            if (request is not null)
            {
                await request.ReadRequestAsync(wire, maxSize: 8, token);
            }

            return request;
        }

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
