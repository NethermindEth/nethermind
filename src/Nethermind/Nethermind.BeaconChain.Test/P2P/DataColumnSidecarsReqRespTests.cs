// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using NUnit.Framework;
using Snappier;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// Framing and DoS-limit tests for the data column sidecar req/resp protocols, mirroring
/// <c>ReqRespLimitsTests</c>' pattern for the block protocols.
/// </summary>
public class DataColumnSidecarsReqRespTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    [Test]
    public void Metrics_truncated_column_framing_records_one_invalid_message([Values] bool gloas)
    {
        TestDataColumnSidecarsProtocol protocol = new(Spec);
        using MemoryStream input = new(new byte[] { ReqRespFraming.ResponseCode.Success });
        long before = FailureCount(TestDataColumnSidecarsProtocol.ProtocolId, ReqRespFailureReason.InvalidMessage);
        Assert.ThrowsAsync<Eth2ReqRespException>(() => gloas ? protocol.ReadGloasSidecarsAsync(input, 1) : protocol.ReadSidecarsAsync(input, 1));
        Assert.That(FailureCount(TestDataColumnSidecarsProtocol.ProtocolId, ReqRespFailureReason.InvalidMessage), Is.EqualTo(before + 1));
    }

    [TestCase(true, TestName = "Response_exceeding_the_chunk_limit_is_rejected_and_the_stream_closed")]
    [TestCase(false, TestName = "Response_at_exactly_the_chunk_limit_is_accepted")]
    public async Task Response_chunk_count_is_bounded(bool exceeds)
    {
        const int maxSidecars = 3;
        TestDataColumnSidecarsProtocol protocol = new(Spec);
        using MemoryStream stream = new();
        for (int i = 0; i < maxSidecars + (exceeds ? 2 : 0); i++)
        {
            await WriteSidecarChunkAsync(stream, DataColumnSidecarTestFixture.BuildValidSidecar(5,
                slot: (exceeds ? 1_000UL : 2_000UL) + (ulong)i, seed: (byte)((exceeds ? 0x40 : 0x50) + i)));
        }

        await ReqRespTestChannel.AssertChunkLimitAsync(stream, maxSidecars, exceeds,
            () => protocol.ReadSidecarsAsync(stream, maxSidecars),
            () => FailureCount(TestDataColumnSidecarsProtocol.ProtocolId, ReqRespFailureReason.LimitExceeded));
    }

    [Test]
    public async Task A_chunk_with_mismatched_fork_digest_context_bytes_is_rejected()
    {
        TestDataColumnSidecarsProtocol protocol = new(Spec);
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(5, slot: 3_000);

        using MemoryStream stream = new();
        byte[] wrongContext = [0xDE, 0xAD, 0xBE, 0xEF];
        await ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, wrongContext, DataColumnSidecar.Encode(sidecar), default);
        stream.Position = 0;

        Eth2ReqRespException ex = Assert.ThrowsAsync<Eth2ReqRespException>(() => protocol.ReadSidecarsAsync(stream, 10))!;
        Assert.That(ex.Message, Does.Contain("context bytes"));
    }

    [TestCase(0)]
    [TestCase(Eip7594DasConstants.NumberOfColumns + 1)]
    public void DialAsync_rejects_an_invalid_columns_count_before_writing_to_the_wire(int columnCount)
    {
        DataColumnSidecarsByRangeProtocol protocol = new(Spec, new DataColumnSidecarPool(), null!);
        DataColumnSidecarsByRangeRequest request = new() { StartSlot = 0, Count = 1, Columns = new ulong[columnCount] };

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => protocol.DialAsync(null!, null!, new(request, Gloas: false)));
    }

    [Test]
    public async Task By_range_serves_only_the_canonical_block_at_each_slot_whatever_the_arrival_order([Values] bool canonicalArrivesFirst)
    {
        const ulong startSlot = 13_410_304;
        const ulong column = 5;
        const ulong canonicalProposer = 1;
        const ulong competingProposer = 2;
        DataColumnSidecarPool pool = new();
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());

        // The middle slot has only a competing block; the last slot reverses the first slot's arrival order.
        AddCompetingBlocks(pool, store, startSlot, canonicalArrivesFirst, hasCanonical: true);
        AddCompetingBlocks(pool, store, startSlot + 1, canonicalArrivesFirst, hasCanonical: false);
        AddCompetingBlocks(pool, store, startSlot + 2, !canonicalArrivesFirst, hasCanonical: true);

        IReadOnlyList<DataColumnSidecar> served = await RequestRangeAsync(pool, store, startSlot, count: 3, [column]);

        Assert.That(served.Select(static s => (s.SignedBlockHeader!.Message!.Slot, s.SignedBlockHeader.Message.ProposerIndex)),
            Is.EqualTo(new[] { (startSlot, canonicalProposer), (startSlot + 2, canonicalProposer) }));

        static void AddCompetingBlocks(DataColumnSidecarPool pool, BeaconChainStore store, ulong slot, bool canonicalFirst, bool hasCanonical)
        {
            Hash256 canonicalRoot = Keccak.Compute($"canonical {slot}");
            Hash256 competingRoot = Keccak.Compute($"competing {slot}");
            DataColumnSidecar canonical = DataColumnSidecarTestFixture.BuildValidSidecar(column, slot, canonicalProposer, blobCount: 1, seed: 0x21);
            DataColumnSidecar competing = DataColumnSidecarTestFixture.BuildValidSidecar(column, slot, competingProposer, blobCount: 1, seed: 0x22);
            if (canonicalFirst) pool.Add(canonicalRoot, slot, canonical);
            pool.Add(competingRoot, slot, competing);
            if (!canonicalFirst) pool.Add(canonicalRoot, slot, canonical);
            if (hasCanonical) store.SetCanonicalRoot(slot, canonicalRoot);
        }
    }

    [Test]
    public async Task By_range_serves_each_requested_column_once_in_slot_then_column_order()
    {
        const ulong startSlot = 13_410_304;
        DataColumnSidecarPool pool = new();
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        for (ulong slot = startSlot; slot < startSlot + 2; slot++)
        {
            Hash256 root = Keccak.Compute($"canonical {slot}");
            pool.Add(root, slot, DataColumnSidecarTestFixture.BuildValidSidecar(5, slot, blobCount: 1));
            pool.Add(root, slot, DataColumnSidecarTestFixture.BuildValidSidecar(7, slot, blobCount: 1));
            store.SetCanonicalRoot(slot, root);
        }

        // Column 9 is not held, so it is skipped.
        IReadOnlyList<DataColumnSidecar> served = await RequestRangeAsync(pool, store, startSlot, count: 2, [7, 9, 5, 5]);

        Assert.That(served.Select(static s => (s.SignedBlockHeader!.Message!.Slot, s.Index)),
            Is.EqualTo(new[] { (startSlot, 5UL), (startSlot, 7UL), (startSlot + 1, 5UL), (startSlot + 1, 7UL) }));
    }

    [Test]
    public async Task By_range_serves_past_MaxRequestBlocks_slots_since_only_sidecars_are_capped([Values(BlocksProtocolBase.MaxRequestBlocks + 1, ulong.MaxValue)] ulong requestedCount)
    {
        const ulong startSlot = 13_410_304;
        const ulong column = 5;
        const ulong slotCount = BlocksProtocolBase.MaxRequestBlocks + 1;
        DataColumnSidecarPool pool = new();
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(column, startSlot, blobCount: 1);
        for (ulong slot = startSlot; slot < startSlot + slotCount; slot++)
        {
            Hash256 root = Keccak.Compute($"canonical {slot}");
            pool.Add(root, slot, sidecar);
            store.SetCanonicalRoot(slot, root);
        }

        IReadOnlyList<DataColumnSidecar> served = await RequestRangeAsync(pool, store, startSlot, requestedCount, [column]);

        Assert.That(served, Has.Count.EqualTo(slotCount));
    }

    [Test]
    public void By_range_is_resource_unavailable_when_no_sidecar_lies_in_the_slots_it_walks()
    {
        const ulong startSlot = 13_410_304;
        const ulong column = 5;
        ulong heldSlot = startSlot + DataColumnSidecarsProtocolBase.MaxRequestDataColumnSidecars;
        DataColumnSidecarPool pool = new();
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        Hash256 root = Keccak.Compute("past the walk");
        pool.Add(root, heldSlot, DataColumnSidecarTestFixture.BuildValidSidecar(column, heldSlot, blobCount: 1));
        store.SetCanonicalRoot(heldSlot, root);

        Eth2ReqRespException? refused = Assert.ThrowsAsync<Eth2ReqRespException>(() => RequestRangeAsync(pool, store, startSlot, heldSlot - startSlot + 1, [column]));

        Assert.That(refused!.ResponseCode, Is.EqualTo(ReqRespFraming.ResponseCode.ResourceUnavailable), "an empty reply would claim the held sidecar's slot is empty");
    }

    /// <summary>
    /// fulu/p2p-interface.md: a peer unable to reply within <c>data_column_serve_range</c> SHOULD answer ResourceUnavailable,
    /// so a requester asks elsewhere instead of reading an incomplete response as the columns that exist.
    /// </summary>
    [TestCase(-1, 2, 1UL, true, TestName = "Inside the serve range from one slot below the complete columns")]
    [TestCase(-1, 1, 1UL, true, TestName = "Inside the serve range at the single slot below the complete columns")]
    [TestCase(0, 2, 1UL, false, TestName = "Inside the serve range from the first complete slot")]
    [TestCase(-3, 5, 1UL, true, TestName = "Inside the serve range across slots below the complete columns")]
    [TestCase(-3, 3, 0UL, false, TestName = "Wholly below the serve range")]
    [TestCase(-3, 5, 0UL, false, TestName = "Below the serve range up to the complete columns")]
    [TestCase(-35, 3, 1UL, false, TestName = "Ending one slot below the serve range")]
    public async Task By_range_answers_resource_unavailable_only_when_the_serve_range_part_starts_below_the_complete_columns(int startOffset, int count, ulong serveRangeEpochsBelowFirstHeld, bool unavailable)
    {
        const ulong firstHeldSlot = 13_410_304;
        const ulong column = 5;
        DataColumnSidecarPool pool = new();
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        for (ulong slot = firstHeldSlot; slot < firstHeldSlot + 2; slot++)
        {
            Hash256 root = Keccak.Compute($"canonical {slot}");
            pool.Add(root, slot, DataColumnSidecarTestFixture.BuildValidSidecar(column, slot, blobCount: 1));
            store.SetCanonicalRoot(slot, root);
        }

        // firstHeldSlot opens an epoch, so the serve range starts at it or whole epochs below it.
        ulong currentEpoch = Spec.GetEpoch(firstHeldSlot) - serveRangeEpochsBelowFirstHeld + Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests;
        SlotClock clock = new(Spec, new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)(Spec.GenesisTime + currentEpoch * Spec.SlotsPerEpoch * Spec.SecondsPerSlot)).UtcDateTime));
        long invalidBefore = FailureCount(ByRangeId, ReqRespFailureReason.InvalidMessage);
        ulong startSlot = (ulong)((long)firstHeldSlot + startOffset);

        Task<IReadOnlyList<DataColumnSidecar>> request = RequestRangeAsync(pool, store, startSlot, (ulong)count, [column], clock);

        if (unavailable)
        {
            Eth2ReqRespException? error = Assert.ThrowsAsync<Eth2ReqRespException>(async () => await request);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(error!.ResponseCode, Is.EqualTo(ReqRespFraming.ResponseCode.ResourceUnavailable));
                Assert.That(FailureCount(ByRangeId, ReqRespFailureReason.InvalidMessage), Is.EqualTo(invalidBefore), "an honest request for columns this node lacks is not the requester's fault");
            }
        }
        else
        {
            ulong firstServed = Math.Max(startSlot, firstHeldSlot);
            ulong lastServed = Math.Min(startSlot + (ulong)count - 1, firstHeldSlot + 1);
            ulong[] expected = firstServed <= lastServed ? [.. Enumerable.Range(0, (int)(lastServed - firstServed + 1)).Select(i => firstServed + (ulong)i)] : [];
            Assert.That((await request).Select(static s => s.SignedBlockHeader!.Message!.Slot), Is.EqualTo(expected));
        }
    }

    // fulu/p2p-interface.md DataColumnSidecarsByRange: held sidecars below the required serve range may still be served.
    [Test]
    public async Task By_range_serves_held_columns_below_the_serve_range()
    {
        const ulong serveFrom = 13_410_304;
        const ulong column = 5;
        DataColumnSidecarPool pool = new();
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        for (ulong slot = serveFrom - 1; slot <= serveFrom; slot++)
        {
            Hash256 root = Keccak.Compute($"canonical {slot}");
            pool.Add(root, slot, DataColumnSidecarTestFixture.BuildValidSidecar(column, slot, blobCount: 1));
            store.SetCanonicalRoot(slot, root);
        }

        ulong currentEpoch = Spec.GetEpoch(serveFrom) + Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests;
        SlotClock clock = new(Spec, new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)(Spec.GenesisTime + currentEpoch * Spec.SlotsPerEpoch * Spec.SecondsPerSlot)).UtcDateTime));

        IReadOnlyList<DataColumnSidecar> served = await RequestRangeAsync(pool, store, serveFrom - 1, 2, [column], clock);

        Assert.That(served.Select(static s => s.SignedBlockHeader!.Message!.Slot), Is.EqualTo(new[] { serveFrom - 1, serveFrom }));
    }

    /// <summary>
    /// <c>data_column_serve_range</c> ends at the current slot, so a request wholly after it asks nothing this node must serve:
    /// even a node that holds no columns answers it with an empty response, not ResourceUnavailable.
    /// </summary>
    [Test]
    public async Task By_range_serves_a_request_wholly_after_the_current_slot_empty([Values] bool held)
    {
        const ulong currentSlot = 13_410_304;
        SlotClock clock = new(Spec, new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)(Spec.GenesisTime + currentSlot * Spec.SecondsPerSlot)).UtcDateTime));

        DataColumnSidecarPool pool = new();
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        if (held)
        {
            Hash256 root = Keccak.Compute("future columns");
            pool.Add(root, currentSlot + 1, DataColumnSidecarTestFixture.BuildValidSidecar(5, currentSlot + 1, blobCount: 1));
            store.SetCanonicalRoot(currentSlot + 1, root);
        }

        IReadOnlyList<DataColumnSidecar> served = await RequestRangeAsync(pool, store, currentSlot + 1, 4, [5], clock);

        Assert.That(served, Is.Empty);
    }

    /// <summary>
    /// The reader bounds a chunk's wire bytes by a function of its declared SSZ length; the largest sidecar its epoch
    /// permits, of incompressible cells, is the honest worst case that bound must still admit.
    /// </summary>
    [Test]
    public async Task The_largest_sidecar_its_epoch_permits_is_served_and_read_back_whole()
    {
        const ulong slot = 13_410_304;
        // An extension-half column: its cells are erasure-coded, so they do not compress like the blob's own half.
        const ulong column = 100;
        int maxBlobs = (int)Spec.GetBlobParameters(Spec.GetEpoch(slot))!.Value.MaxBlobsPerBlock;
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(column, slot, blobCount: maxBlobs, seed: 1);
        byte[] ssz = DataColumnSidecar.Encode(sidecar);
        Assert.That(Snappy.CompressToArray(ssz), Has.Length.GreaterThan(ssz.Length * 99 / 100), "fixture: the cells must not compress, or the wire bound is never approached");
        DataColumnSidecarPool pool = new();
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        Hash256 root = Keccak.Compute("canonical");
        pool.Add(root, slot, sidecar);
        store.SetCanonicalRoot(slot, root);

        IReadOnlyList<DataColumnSidecar> served = await RequestRangeAsync(pool, store, slot, 1, [column]);

        Assert.That(served.Select(static s => DataColumnSidecar.Encode(s)), Is.EqualTo(new[] { ssz }));
    }

    [Test]
    public void DialAsync_rejects_more_identifiers_than_MaxRequestBlocks_before_writing_to_the_wire()
    {
        DataColumnSidecarsByRootProtocol protocol = new(Spec, new DataColumnSidecarPool());
        DataColumnsByRootIdentifier[] request = new DataColumnsByRootIdentifier[BlocksProtocolBase.MaxRequestBlocks + 1];
        for (int i = 0; i < request.Length; i++)
        {
            request[i] = new DataColumnsByRootIdentifier { BlockRoot = Hash256.Zero, Columns = [0] };
        }

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => protocol.DialAsync(null!, null!, new(request, Gloas: false)));
    }

    [Test]
    public async Task Exactly_MaxRequestBlocks_identifiers_at_full_columns_hits_MaxRequestDataColumnSidecars_exactly_and_is_served([Values] bool gloas)
    {
        // MaxRequestBlocks (128) identifiers x NumberOfColumns (128) columns each = exactly
        // MaxRequestDataColumnSidecars (16384): the largest ask both sides must accept, list offsets included.
        ulong[] allColumns = [.. Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(static i => (ulong)i)];
        DataColumnsByRootIdentifier[] request = [.. Enumerable.Range(0, (int)BlocksProtocolBase.MaxRequestBlocks)
            .Select(i => new DataColumnsByRootIdentifier { BlockRoot = Keccak.Compute($"unknown block {i}"), Columns = allColumns })];
        Assert.That((ulong)request.Length * (ulong)allColumns.Length, Is.EqualTo(DataColumnSidecarsProtocolBase.MaxRequestDataColumnSidecars));

        DataColumnSidecarsByRootProtocol protocol = new(Spec, new DataColumnSidecarPool());
        ISessionContext context = ReqRespTestChannel.Context();
        Channel channel = new();
        Task listen = ReqRespTestChannel.ListenThenCloseAsync(protocol, channel.Reverse, context);

        ForkedDataColumnSidecars served = await protocol.DialAsync(channel, context, new(request, gloas));
        await listen;

        Assert.That(served.Fulu.Count + served.Gloas.Count, Is.Zero);
    }

    [Test]
    public void The_root_dial_refuses_a_repeated_root_and_column_before_writing_to_the_wire([Values] bool splitOverTwoIdentifiers)
    {
        DataColumnsByRootIdentifier[] request = splitOverTwoIdentifiers
            ? [new() { BlockRoot = Hash256.Zero, Columns = [3, 5] }, new() { BlockRoot = Hash256.Zero, Columns = [3] }]
            : [new() { BlockRoot = Hash256.Zero, Columns = [3, 3] }];

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new DataColumnSidecarsByRootProtocol(Spec, new DataColumnSidecarPool()).DialAsync(null!, null!, new(request, Gloas: false)));
    }

    /// <summary>A reply cut short must leave the caller every chunk read before the cut, or the batch asks for them again.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task The_real_dial_hands_out_each_chunk_read_before_the_reply_is_cut_short(CancellationToken token)
    {
        DataColumnSidecar[] good = [.. Enumerable.Range(0, 3).Select(i => DataColumnSidecarTestFixture.BuildValidSidecar(i % 2 == 0 ? 3UL : 4UL, slot: 100 + (ulong)i))];
        byte[] cut = await EncodeChunkAsync(DataColumnSidecarTestFixture.BuildValidSidecar(3, slot: 103));
        List<DataColumnSidecar> seen = [];

        Eth2ReqRespException? thrown = Assert.ThrowsAsync<Eth2ReqRespException>(() => DialRangeAsync(good, cut[..(cut.Length / 2)], seen.Add, token: token));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seen.Select(Key), Is.EqualTo(good.Select(Key)), "a chunk fully read is kept when a later chunk is truncated");
            Assert.That(thrown!.Message, Does.StartWith("Truncated response chunk"));
        }
    }

    /// <summary>A chunk outside the requested slots or columns is a protocol violation and must never reach the caller's pool of kept sidecars.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task The_real_dial_refuses_a_chunk_outside_the_request_before_handing_it_out([Values] bool unrequestedColumn, CancellationToken token)
    {
        DataColumnSidecar valid = DataColumnSidecarTestFixture.BuildValidSidecar(3, slot: 100);
        DataColumnSidecar stray = unrequestedColumn
            ? DataColumnSidecarTestFixture.BuildValidSidecar(9, slot: 101)
            : DataColumnSidecarTestFixture.BuildValidSidecar(3, slot: 104);
        List<DataColumnSidecar> seen = [];

        Assert.ThrowsAsync<Eth2ReqRespException>(() => DialRangeAsync([valid, stray], [], seen.Add, token: token));

        Assert.That(seen.Select(Key), Is.EqualTo(new[] { Key(valid) }));
    }

    private static (ulong Slot, ulong Column) Key(DataColumnSidecar sidecar) => (sidecar.SignedBlockHeader!.Message!.Slot, sidecar.Index);

    /// <summary>The request budget must cut a reply even when each chunk arrives within its own timeout.</summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task The_real_dial_is_cut_at_the_budget_scaled_to_the_request_though_each_chunk_arrives_in_time(CancellationToken token)
    {
        DataColumnSidecar[] chunks = [.. new ulong[] { 3, 4, 5 }.Select(column => DataColumnSidecarTestFixture.BuildValidSidecar(column, slot: 100))];
        DataColumnSidecarsByRangeRequest request = new() { StartSlot = 100, Count = 1, Columns = [3, 4, 5] };
        Assert.That(DataColumnSidecarsByRangeProtocol.ResponseBudget(1, 3), Is.EqualTo(TimeSpan.FromSeconds(16)));
        List<DataColumnSidecar> seen = [];
        DataColumnSidecarsByRangeProtocol protocol = new(Spec, new DataColumnSidecarPool(), new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()))
        {
            TtfbTimeout = TimeSpan.FromMilliseconds(500),
            RespTimeout = TimeSpan.FromSeconds(2),
        };

        ReqRespTimeoutException? cut = Assert.ThrowsAsync<ReqRespTimeoutException>(() => DialRangeAsync(chunks, [], seen.Add, request, TimeSpan.FromMilliseconds(1_400), token, protocol));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seen, Has.Count.EqualTo(2), "the third chunk was never read");
            Assert.That(cut!.Message, Is.EqualTo("timed out after 3.5 s, the bound for the whole response, with 2 chunks read"));
        }
    }

    private static async Task DialRangeAsync(DataColumnSidecar[] whole, byte[] trailingBytes, Action<DataColumnSidecar> onSidecar, DataColumnSidecarsByRangeRequest? request = null, TimeSpan chunkGap = default, CancellationToken token = default, DataColumnSidecarsByRangeProtocol? protocol = null)
    {
        protocol ??= new(Spec, new DataColumnSidecarPool(), new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()));
        ISessionContext context = ReqRespTestChannel.Context();
        Channel channel = new();
        using CancellationTokenSource serverStop = CancellationTokenSource.CreateLinkedTokenSource(token);

        Task server = Task.Run(async () =>
        {
            Stream stream = new ChannelStreamAdapter(channel.Reverse);
            byte[] scratch = new byte[4096];
            while (await stream.ReadAsync(scratch, serverStop.Token) > 0)
            {
            }

            try
            {
                foreach (DataColumnSidecar sidecar in whole)
                {
                    await Task.Delay(chunkGap, serverStop.Token);
                    await WriteSidecarChunkAsync(stream, sidecar, serverStop.Token);
                }

                await stream.WriteAsync(trailingBytes, serverStop.Token);
                await channel.Reverse.WriteEofAsync(serverStop.Token);
            }
            catch (OperationCanceledException) when (chunkGap > TimeSpan.Zero)
            {
                // The dialer cut the reply and stopped reading while this server was still dripping chunks.
            }
        }, token);

        request ??= new() { StartSlot = 100, Count = 4, Columns = [3, 4] };
        try
        {
            await protocol.DialAsync(channel, context, new(request, Gloas: false, onSidecar));
        }
        finally
        {
            await serverStop.CancelAsync();
            try
            {
                await server;
            }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
                // Raised only by the stop above, once the dial is over.
            }
        }
    }

    private static async Task<byte[]> EncodeChunkAsync(DataColumnSidecar sidecar)
    {
        using MemoryStream stream = new();
        await WriteSidecarChunkAsync(stream, sidecar);
        return stream.ToArray();
    }

    internal static async Task<IReadOnlyList<DataColumnSidecar>> RequestRangeAsync(DataColumnSidecarPool pool, BeaconChainStore store, ulong startSlot, ulong count, ulong[] columns, SlotClock? clock = null)
    {
        DataColumnSidecarsByRangeProtocol protocol = new(Spec, pool, store, clock);
        ISessionContext context = ReqRespTestChannel.Context();

        Channel channel = new();
        Task listen = ReqRespTestChannel.ListenThenCloseAsync(protocol, channel.Reverse, context);
        ForkedDataColumnSidecars served = await protocol.DialAsync(channel, context, new(new DataColumnSidecarsByRangeRequest { StartSlot = startSlot, Count = count, Columns = columns }, Gloas: false));
        await listen;
        return served.Fulu;
    }

    private const string ByRangeId = "/eth2/beacon_chain/req/data_column_sidecars_by_range/1/ssz_snappy";

    private static long FailureCount(string protocolId, ReqRespFailureReason reason) =>
        Metrics.BeaconChainReqRespFailures.TryGetValue(new ReqRespFailureKey(protocolId, reason), out long count) ? count : 0;

    private static Task WriteSidecarChunkAsync(Stream stream, DataColumnSidecar sidecar, CancellationToken token = default)
    {
        byte[] contextBytes = ForkDigest.Compute(Spec, Spec.GetEpoch(sidecar.SignedBlockHeader!.Message!.Slot));
        return ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, contextBytes, DataColumnSidecar.Encode(sidecar), token);
    }

    /// <summary>Exposes the protected chunked-response reader for direct testing.</summary>
    private sealed class TestDataColumnSidecarsProtocol(BeaconChainSpec spec) : DataColumnSidecarsProtocolBase(spec)
    {
        public const string ProtocolId = "/test/data-column-sidecars-limits/1";

        public async Task<IReadOnlyList<DataColumnSidecar>> ReadSidecarsAsync(Stream response, int maxSidecars)
        {
            RequestTiming timing = new();
            IReadOnlyList<DataColumnSidecar> sidecars = await ReadSidecarChunksAsync(response, maxSidecars, ProtocolId, timing: timing);
            Assert.That(timing.Chunks, Is.EqualTo(sidecars.Count));
            return sidecars;
        }

        public Task<IReadOnlyList<DataColumnSidecarGloas>> ReadGloasSidecarsAsync(MemoryStream input, int maxSidecars) =>
            ReadGloasSidecarChunksAsync(input, maxSidecars, ProtocolId);
    }
}
