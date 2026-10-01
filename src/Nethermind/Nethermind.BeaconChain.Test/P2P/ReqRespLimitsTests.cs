// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Multiformats.Address;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using NUnit.Framework;
using Libp2pPublicKey = Nethermind.Libp2p.Core.Dto.PublicKey;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// Exercises the req/resp DoS limits directly against <see cref="ReqRespProtocolBase"/> and
/// <see cref="BlocksProtocolBase"/>: no libp2p channel or real elapsed-time sleep is needed since
/// every method under test already takes a <see cref="Stream"/> and/or a caller-supplied deadline.
/// </summary>
public class ReqRespLimitsTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    [Test]
    public async Task Concurrent_inbound_requests_beyond_the_cap_are_refused()
    {
        TestReqRespProtocol protocol = new();
        ISessionContext peerA = FakeSessionContext.ForNewPeer();

        long before = FailureCount(TestReqRespProtocol.ProtocolId, ReqRespFailureReason.LimitExceeded);

        IAsyncDisposable? slot1 = protocol.TryEnter(peerA, TestReqRespProtocol.ProtocolId);
        IAsyncDisposable? slot2 = protocol.TryEnter(peerA, TestReqRespProtocol.ProtocolId);
        Assert.That(slot1, Is.Not.Null, "first concurrent request admitted");
        Assert.That(slot2, Is.Not.Null, "second concurrent request admitted (at the cap)");

        // A breakage that removed the cap (e.g. always returning a slot) would let this pass too,
        // so the sibling assert below on FailureCount is what actually pins the cap at 2.
        IAsyncDisposable? slot3 = protocol.TryEnter(peerA, TestReqRespProtocol.ProtocolId);
        Assert.That(slot3, Is.Null, "third concurrent request from the same peer refused");
        Assert.That(FailureCount(TestReqRespProtocol.ProtocolId, ReqRespFailureReason.LimitExceeded), Is.EqualTo(before + 1), "limit violation recorded");

        // A different peer has its own budget: the cap is per-peer, not global to the protocol.
        ISessionContext peerB = FakeSessionContext.ForNewPeer();
        IAsyncDisposable? otherPeerSlot = protocol.TryEnter(peerB, TestReqRespProtocol.ProtocolId);
        Assert.That(otherPeerSlot, Is.Not.Null, "a different peer is not affected by peer A's cap");

        // Releasing a slot frees budget for the same peer to be admitted again.
        await slot1!.DisposeAsync();
        IAsyncDisposable? slot4 = protocol.TryEnter(peerA, TestReqRespProtocol.ProtocolId);
        Assert.That(slot4, Is.Not.Null, "releasing a slot allows another request to be admitted");

        await slot2!.DisposeAsync();
        await slot4!.DisposeAsync();
        await otherPeerSlot!.DisposeAsync();
    }

    [Test]
    public async Task Response_exceeding_the_chunk_limit_is_rejected_and_the_stream_closed()
    {
        const int maxBlocks = 3;
        TestBlocksProtocol protocol = new(Spec);
        // Two more than maxBlocks, so bytes remain unread on the stream after the throw even though
        // the chunk that trips the cap is itself fully consumed before the count check rejects it.
        (_, _, SignedBeaconBlock[] chain) =
            TestChain.BuildLinkedChain(1_000, 1_001, 1_002, 1_003, 1_004, 1_005);

        using MemoryStream stream = new();
        foreach (SignedBeaconBlock block in chain)
        {
            await WriteBlockChunkAsync(stream, block);
        }

        stream.Position = 0;

        long before = FailureCount(TestBlocksProtocol.ProtocolId, ReqRespFailureReason.LimitExceeded);

        // If the cap were not enforced, this would return all 5 blocks instead of throwing.
        Eth2ReqRespException? thrown = Assert.ThrowsAsync<Eth2ReqRespException>(() => protocol.ReadBlocksAsync(stream, maxBlocks));
        Assert.That(thrown!.Message, Does.Contain(maxBlocks.ToString()));
        Assert.That(FailureCount(TestBlocksProtocol.ProtocolId, ReqRespFailureReason.LimitExceeded), Is.EqualTo(before + 1), "limit violation recorded");

        // The stream was abandoned mid-response: bytes for the trailing chunks are still unread,
        // proving the caller stopped consuming rather than draining and discarding them.
        Assert.That(stream.Position, Is.LessThan(stream.Length), "stream was closed to the peer before it was fully drained");
    }

    [Test]
    public async Task Chunk_count_cap_is_enforced_for_a_range_request()
    {
        // Mirrors BeaconBlocksByRangeProtocolV2.DialAsync's own cap arithmetic: the smaller of the
        // requested count and the spec's MaxRequestBlocks, not MaxRequestBlocks unconditionally.
        const ulong requestedCount = 2;
        int maxBlocks = (int)Math.Min(requestedCount, BlocksProtocolBase.MaxRequestBlocks);
        Assert.That(maxBlocks, Is.LessThan((int)BlocksProtocolBase.MaxRequestBlocks), "test is only meaningful below the protocol-wide cap");

        TestBlocksProtocol protocol = new(Spec);
        (_, _, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(3_000, 3_001, 3_002, 3_003); // more than requestedCount

        using MemoryStream stream = new();
        foreach (SignedBeaconBlock block in chain)
        {
            await WriteBlockChunkAsync(stream, block);
        }

        stream.Position = 0;

        // If the range cap fell back to the protocol-wide MaxRequestBlocks (128) instead of the
        // smaller requested count, this would return all 4 blocks instead of throwing.
        Eth2ReqRespException ex = Assert.ThrowsAsync<Eth2ReqRespException>(
            () => protocol.ReadBlocksAsync(stream, maxBlocks))!;

        // Assert on the reason, not merely that something threw: a framing or decode fault would
        // also surface as this exception type and would otherwise pass for enforcement.
        Assert.That(ex.Message, Does.Contain(maxBlocks.ToString()),
            "the rejection must name the cap it exceeded so an operator can attribute it");
    }

    [Test]
    public void Read_response_chunk_abandons_a_stream_that_stalls_past_the_caller_deadline()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(20));
        using NeverEndingStream stream = new();

        Stopwatch stopwatch = Stopwatch.StartNew();
        // A caller-supplied token that is never honored would hang this call forever instead of
        // throwing quickly, so this pins that ReadResponseChunkAsync actually observes it.
        Assert.CatchAsync<OperationCanceledException>(() => ReqRespFraming.ReadResponseChunkAsync(stream, 0, 8, cts.Token));
        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "abandoned promptly rather than hanging");
    }

    [Test]
    public async Task Overall_deadline_abandons_a_streamed_response_that_drip_feeds_chunks_forever()
    {
        // Each inter-chunk gap (20 ms) is comfortably under the per-chunk RESP_TIMEOUT (10 s), so a
        // peer re-arming that per-chunk timer by trickling one valid block every 20 ms would never
        // trip it. Only a ceiling on the whole exchange (here shortened to 80 ms for the test) can
        // stop it; if StartBoundedTimeout stopped linking that ceiling in, this would instead read
        // all 10 blocks and return normally within the run.
        const int blockCount = 10;
        TestBlocksProtocol protocol = new(Spec);
        (_, _, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(2_000, [.. SlotRange(2_001, blockCount)]);

        List<byte[]> wireChunks = [];
        foreach (SignedBeaconBlock block in chain)
        {
            using MemoryStream buffer = new();
            await WriteBlockChunkAsync(buffer, block);
            wireChunks.Add(buffer.ToArray());
        }

        using DrippingStream stream = new(wireChunks, TimeSpan.FromMilliseconds(20));
        long before = FailureCount(TestBlocksProtocol.ProtocolId, ReqRespFailureReason.Timeout);

        ReqRespTimeoutException? cut = Assert.ThrowsAsync<ReqRespTimeoutException>(() =>
            protocol.ReadBlocksAsync(stream, blockCount, TimeSpan.FromMilliseconds(80)));
        Assert.That(FailureCount(TestBlocksProtocol.ProtocolId, ReqRespFailureReason.Timeout), Is.EqualTo(before + 1), "timeout recorded");
        Assert.That(cut!.Message, Does.Match(@"^timed out after 0\.1 s, the bound for the whole response, with \d chunks read$"));
    }

    /// <summary>A timeout names the bound that fired: nothing within the first-chunk bound, or a later chunk not within the bound between chunks.</summary>
    [TestCase(1, 16_000, "timed out after 15 s waiting for the first chunk")]
    [TestCase(2, 12_000, "timed out after 10 s reading chunk 2")]
    [CancelAfter(60_000)]
    public async Task A_read_cut_by_a_chunk_bound_names_that_bound(int blocks, int delayBeforeEachChunkMs, string expected)
    {
        TestBlocksProtocol protocol = new(Spec);
        (_, _, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(2_000, [.. SlotRange(2_001, blocks)]);
        List<byte[]> wireChunks = [];
        foreach (SignedBeaconBlock block in chain)
        {
            using MemoryStream buffer = new();
            await WriteBlockChunkAsync(buffer, block);
            wireChunks.Add(buffer.ToArray());
        }

        await using DrippingStream response = new(wireChunks, TimeSpan.FromMilliseconds(delayBeforeEachChunkMs));

        ReqRespTimeoutException? cut = Assert.ThrowsAsync<ReqRespTimeoutException>(() => protocol.ReadBlocksAsync(response, blocks));

        Assert.That(cut!.Message, Is.EqualTo(expected));
    }

    /// <summary>A request the peer never reads is cut at the write bound, and the failure says so rather than blaming the response.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void A_request_the_peer_never_reads_times_out_naming_the_write()
    {
        BeaconBlocksByRootProtocolV2 protocol = new(Spec, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()));

        ReqRespTimeoutException? cut = Assert.ThrowsAsync<ReqRespTimeoutException>(() => protocol.DialAsync(new Channel(), FakeSessionContext.ForNewPeer(), [Hash256.Zero]));

        Assert.That(cut!.Message, Is.EqualTo("timed out after 10 s writing the request"));
    }

    /// <summary>A single-chunk exchange has one bound for the request and its answer.</summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task A_single_chunk_request_never_answered_times_out_naming_the_response([Values] bool metadata)
    {
        Eth2PingProtocol protocol = new(new LocalMetadataSource());
        Channel channel = new();
        // Reads the request and never answers.
        Task drain = Task.Run(async () =>
        {
            while ((await channel.Reverse.ReadAsync(1, ReadBlockingMode.WaitAny)).Result == IOResult.Ok)
            {
            }
        });

        ReqRespTimeoutException? cut = Assert.ThrowsAsync<ReqRespTimeoutException>(() => metadata
            ? new MetaDataProtocolV3(new LocalMetadataSource()).DialAsync(channel, FakeSessionContext.ForNewPeer(), 0)
            : protocol.DialAsync(channel, FakeSessionContext.ForNewPeer(), 7));

        Assert.That(cut!.Message, Is.EqualTo("timed out after 15 s waiting for the response"));
        await channel.CloseAsync();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public enum ResponseKind { Blocks, FuluColumns, GloasColumns, Envelopes }

    [Test]
    public async Task A_block_response_counts_each_read_chunk()
    {
        (_, _, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(2_000, 2_001);
        using MemoryStream response = new();
        await WriteBlockChunkAsync(response, chain[0]);
        response.Position = 0;
        RequestTiming timing = new();

        IReadOnlyList<ForkedSignedBeaconBlock> blocks = await new TestBlocksProtocol(Spec).ReadBlocksAsync(response, 1, timing: timing);

        Assert.That((blocks.Count, timing.Chunks), Is.EqualTo((1, 1)));
    }

    [Test]
    public void Every_response_reader_names_its_overall_bound([Values] ResponseKind kind)
    {
        using NeverEndingStream response = new();
        TimeSpan bound = TimeSpan.FromMilliseconds(80);
        ReqRespTimeoutException? cut = Assert.ThrowsAsync<ReqRespTimeoutException>(() => kind switch
        {
            ResponseKind.Blocks => new TestBlocksProtocol(Spec).ReadBlocksAsync(response, 1, bound),
            ResponseKind.FuluColumns => new TestColumnsProtocol(Spec).ReadAsync(response, bound, gloas: false),
            ResponseKind.GloasColumns => new TestColumnsProtocol(Spec).ReadAsync(response, bound, gloas: true),
            _ => new TestEnvelopesProtocol(Spec).ReadAsync(response, bound),
        });

        Assert.That(cut!.Message, Is.EqualTo("timed out after 0.1 s, the bound for the whole response, with 0 chunks read"));
    }

    private sealed class TestColumnsProtocol(BeaconChainSpec spec) : DataColumnSidecarsProtocolBase(spec)
    {
        public Task ReadAsync(Stream response, TimeSpan bound, bool gloas) => gloas
            ? ReadGloasSidecarChunksAsync(response, 1, "/test/columns/1", bound)
            : ReadSidecarChunksAsync(response, 1, "/test/columns/1", bound);
    }

    private sealed class TestEnvelopesProtocol(BeaconChainSpec spec) : ExecutionPayloadEnvelopesProtocolBase(spec)
    {
        public Task ReadAsync(Stream response, TimeSpan bound) => ReadEnvelopeChunksAsync(response, 1, "/test/envelopes/1", bound);
    }

    private static long FailureCount(string protocolId, ReqRespFailureReason reason) =>
        Metrics.BeaconChainReqRespFailures.TryGetValue(new ReqRespFailureKey(protocolId, reason), out long count) ? count : 0;

    private static Task WriteBlockChunkAsync(Stream stream, SignedBeaconBlock block)
    {
        byte[] contextBytes = ForkDigest.Compute(Spec, Spec.GetEpoch(block.Message!.Slot));
        return ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, contextBytes, SignedBeaconBlock.Encode(block), default);
    }

    private static IEnumerable<ulong> SlotRange(ulong start, int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return start + (ulong)i;
        }
    }

    /// <summary>Exposes the protected inbound-concurrency gate for direct testing.</summary>
    private sealed class TestReqRespProtocol : ReqRespProtocolBase
    {
        public const string ProtocolId = "/test/reqresp-limits/1";

        public IAsyncDisposable? TryEnter(ISessionContext context, string protocolId) => TryEnterInbound(context, protocolId);
    }

    /// <summary>Exposes the protected chunked-response reader for direct testing.</summary>
    private sealed class TestBlocksProtocol(BeaconChainSpec spec) : BlocksProtocolBase(spec)
    {
        public const string ProtocolId = "/test/blocks-limits/1";

        public Task<IReadOnlyList<ForkedSignedBeaconBlock>> ReadBlocksAsync(Stream response, int maxBlocks, TimeSpan? overallTimeout = null, RequestTiming? timing = null) =>
            ReadBlockChunksAsync(response, maxBlocks, ProtocolId, overallTimeout, timing);
    }

    /// <summary>A minimal <see cref="ISessionContext"/> carrying only a synthetic remote peer identity.</summary>
    private sealed class FakeSessionContext : ISessionContext
    {
        private FakeSessionContext(PeerId peerId) =>
            State = new Nethermind.Libp2p.Core.State { RemoteAddress = Multiaddress.Decode($"/p2p/{peerId}") };

        public static FakeSessionContext ForNewPeer()
        {
            byte[] keyBytes = new byte[33];
            Random.Shared.NextBytes(keyBytes);
            PeerId peerId = new(new Libp2pPublicKey { Type = KeyType.Secp256K1, Data = ByteString.CopyFrom(keyBytes) });
            return new FakeSessionContext(peerId);
        }

        public Nethermind.Libp2p.Core.State State { get; }

        public string Id => throw new NotSupportedException();
        public ILocalPeer Peer => throw new NotSupportedException();
        public System.Diagnostics.Activity? Activity => throw new NotSupportedException();
        public UpgradeOptions? UpgradeOptions => throw new NotSupportedException();
        public IEnumerable<IProtocol> SubProtocols => throw new NotSupportedException();

        public Task DialAsync<TProtocol>() where TProtocol : ISessionProtocol => throw new NotSupportedException();
        public Task DialAsync(ISessionProtocol protocol) => throw new NotSupportedException();
        public Task DisconnectAsync() => throw new NotSupportedException();
        public INewSessionContext UpgradeToSession() => throw new NotSupportedException();
        public void ListenerReady(Multiaddress addr) => throw new NotSupportedException();
        public INewConnectionContext CreateConnection() => throw new NotSupportedException();
        public IChannel Upgrade(UpgradeOptions? options = null) => throw new NotSupportedException();
        public IChannel Upgrade(IProtocol specificProtocol, UpgradeOptions? options = null) => throw new NotSupportedException();
        public Task Upgrade(IChannel parentChannel, UpgradeOptions? options = null) => throw new NotSupportedException();
        public Task Upgrade(IChannel parentChannel, IProtocol specificProtocol, UpgradeOptions? options = null) => throw new NotSupportedException();
    }

    /// <summary>A stream whose reads never complete on their own, honoring only the caller's cancellation token.</summary>
    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Serves pre-built chunk byte segments back to back, waiting <paramref name="delayBeforeEachChunk"/> before starting each new one.</summary>
    private sealed class DrippingStream(IReadOnlyList<byte[]> chunks, TimeSpan delayBeforeEachChunk) : Stream
    {
        private int _chunkIndex = -1;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_chunkIndex == -1 || _offset >= chunks[_chunkIndex].Length)
            {
                _chunkIndex++;
                _offset = 0;
                if (_chunkIndex >= chunks.Count)
                {
                    return 0;
                }

                await Task.Delay(delayBeforeEachChunk, cancellationToken);
            }

            byte[] chunk = chunks[_chunkIndex];
            int n = Math.Min(buffer.Length, chunk.Length - _offset);
            chunk.AsSpan(_offset, n).CopyTo(buffer.Span);
            _offset += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
