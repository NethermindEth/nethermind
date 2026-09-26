// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// Framing and DoS-limit tests for the data column sidecar req/resp protocols, mirroring
/// <c>ReqRespLimitsTests</c>' pattern for the block protocols.
/// </summary>
public class DataColumnSidecarsReqRespTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    [Test]
    public async Task Response_exceeding_the_chunk_limit_is_rejected_and_the_stream_closed()
    {
        const int maxSidecars = 3;
        TestDataColumnSidecarsProtocol protocol = new(Spec);

        using MemoryStream stream = new();
        for (int i = 0; i < maxSidecars + 2; i++)
        {
            await WriteSidecarChunkAsync(stream, DataColumnSidecarTestFixture.BuildValidSidecar(5, slot: 1_000 + (ulong)i, seed: (byte)(0x40 + i)));
        }

        stream.Position = 0;

        long before = FailureCount(TestDataColumnSidecarsProtocol.ProtocolId, ReqRespFailureReason.LimitExceeded);

        Eth2ReqRespException? thrown = Assert.ThrowsAsync<Eth2ReqRespException>(() => protocol.ReadSidecarsAsync(stream, maxSidecars));
        Assert.That(thrown!.Message, Does.Contain(maxSidecars.ToString()));
        Assert.That(FailureCount(TestDataColumnSidecarsProtocol.ProtocolId, ReqRespFailureReason.LimitExceeded), Is.EqualTo(before + 1), "limit violation recorded");
        Assert.That(stream.Position, Is.LessThan(stream.Length), "stream was closed to the peer before it was fully drained");
    }

    [Test]
    public async Task Response_at_exactly_the_chunk_limit_is_accepted()
    {
        const int maxSidecars = 3;
        TestDataColumnSidecarsProtocol protocol = new(Spec);

        using MemoryStream stream = new();
        for (int i = 0; i < maxSidecars; i++)
        {
            await WriteSidecarChunkAsync(stream, DataColumnSidecarTestFixture.BuildValidSidecar(5, slot: 2_000 + (ulong)i, seed: (byte)(0x50 + i)));
        }

        stream.Position = 0;

        IReadOnlyList<DataColumnSidecar> sidecars = await protocol.ReadSidecarsAsync(stream, maxSidecars);
        Assert.That(sidecars, Has.Count.EqualTo(maxSidecars));
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
    public async Task By_range_serves_at_most_MaxRequestBlocks_slots([Values(BlocksProtocolBase.MaxRequestBlocks + 1, ulong.MaxValue)] ulong requestedCount)
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

        Assert.That(served, Has.Count.EqualTo(BlocksProtocolBase.MaxRequestBlocks));
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
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new Nethermind.Libp2p.Core.State());
        Channel channel = new();
        Task listen = ListenThenCloseAsync(protocol, channel.Reverse, context);

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

    private static async Task<IReadOnlyList<DataColumnSidecar>> RequestRangeAsync(DataColumnSidecarPool pool, BeaconChainStore store, ulong startSlot, ulong count, ulong[] columns)
    {
        DataColumnSidecarsByRangeProtocol protocol = new(Spec, pool, store);
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new Nethermind.Libp2p.Core.State());

        Channel channel = new();
        Task listen = ListenThenCloseAsync(protocol, channel.Reverse, context);
        ForkedDataColumnSidecars served = await protocol.DialAsync(channel, context, new(new DataColumnSidecarsByRangeRequest { StartSlot = startSlot, Count = count, Columns = columns }, Gloas: false));
        await listen;
        return served.Fulu;
    }

    // The libp2p host closes the response stream once the handler returns; the dial side reads until then.
    private static async Task ListenThenCloseAsync(ISessionListenerProtocol protocol, IChannel channel, ISessionContext context)
    {
        await protocol.ListenAsync(channel, context);
        await channel.WriteEofAsync();
    }

    private static long FailureCount(string protocolId, ReqRespFailureReason reason) =>
        Metrics.BeaconChainReqRespFailures.TryGetValue(new ReqRespFailureKey(protocolId, reason), out long count) ? count : 0;

    private static Task WriteSidecarChunkAsync(Stream stream, DataColumnSidecar sidecar)
    {
        byte[] contextBytes = ForkDigest.Compute(Spec, Spec.GetEpoch(sidecar.SignedBlockHeader!.Message!.Slot));
        return ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, contextBytes, DataColumnSidecar.Encode(sidecar), default);
    }

    /// <summary>Exposes the protected chunked-response reader for direct testing.</summary>
    private sealed class TestDataColumnSidecarsProtocol(BeaconChainSpec spec) : DataColumnSidecarsProtocolBase(spec)
    {
        public const string ProtocolId = "/test/data-column-sidecars-limits/1";

        public Task<IReadOnlyList<DataColumnSidecar>> ReadSidecarsAsync(Stream stream, int maxSidecars) =>
            ReadSidecarChunksAsync(stream, maxSidecars, ProtocolId);
    }
}
