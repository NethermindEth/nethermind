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
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Core;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// The Gloas-shaped <c>data_column_sidecars_by_range</c> and <c>by_root</c> dials and the by-root listen side:
/// gloas/p2p-interface.md Modified <c>DataColumnSidecar</c> and <c>compute_max_data_column_sidecar_size</c>.
/// </summary>
public class GloasColumnReqRespTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Sepolia;
    private static readonly ulong GloasStartSlot = Spec.GloasForkEpoch * Spec.SlotsPerEpoch;

    public enum RefusedChunk
    {
        GloasSlotUnderFuluDigest,
        PreGloasSlotUnderItsOwnDigest,
        FuluChunkUnderGloasDigest,
        OneByteOverComputedBound,
        FewerProofsThanCells,
        MoreProofsThanCells,
        IndexOutOfRange,
        EmptyColumn,
    }

    [Test]
    public async Task A_Gloas_chunk_under_its_Gloas_digest_decodes()
    {
        DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(9, GloasStartSlot + 3);
        using MemoryStream stream = await ChunksAsync((ContextOf(sidecar.Slot), DataColumnSidecarGloas.Encode(sidecar)));

        IReadOnlyList<DataColumnSidecarGloas> read = await new TestProtocol(Spec).ReadGloasAsync(stream, 1);

        Assert.That(read.Select(static s => (s.Index, s.Slot, s.BeaconBlockRoot, s.Column!.Length)),
            Is.EqualTo(new[] { (9UL, GloasStartSlot + 3, DataColumnSidecarGloasTestFixture.BlockRoot, 2) }));
    }

    [Test]
    public async Task The_Gloas_reader_refuses_a_chunk_that_is_not_a_well_formed_Gloas_sidecar_of_its_digest([Values] RefusedChunk refused)
    {
        (byte[] context, byte[] payload, string reason) = refused switch
        {
            RefusedChunk.GloasSlotUnderFuluDigest => (ContextOf(GloasStartSlot - 1), Encode(GloasStartSlot + 1), "context bytes"),
            RefusedChunk.PreGloasSlotUnderItsOwnDigest => (ContextOf(GloasStartSlot - 1), Encode(GloasStartSlot - 1), "pre-Gloas slot"),
            RefusedChunk.FuluChunkUnderGloasDigest => (ContextOf(GloasStartSlot), DataColumnSidecar.Encode(DataColumnSidecarTestFixture.BuildValidSidecar(9, GloasStartSlot, blobCount: 1)), "Malformed Gloas"),
            RefusedChunk.OneByteOverComputedBound => (ContextOf(GloasStartSlot), new byte[DataColumnSidecarGloasSize.ComputeMax(Spec) + 1], "Invalid payload length"),
            RefusedChunk.FewerProofsThanCells => (ContextOf(GloasStartSlot), Encode(GloasStartSlot, static s => s.KzgProofs = s.KzgProofs![..1]), "structural"),
            RefusedChunk.MoreProofsThanCells => (ContextOf(GloasStartSlot), Encode(GloasStartSlot, static s => s.KzgProofs = [.. s.KzgProofs!, s.KzgProofs![0]]), "structural"),
            RefusedChunk.IndexOutOfRange => (ContextOf(GloasStartSlot), Encode(GloasStartSlot, static s => s.Index = Eip7594DasConstants.NumberOfColumns), "structural"),
            RefusedChunk.EmptyColumn => (ContextOf(GloasStartSlot), Encode(GloasStartSlot, static s => (s.Column, s.KzgProofs) = ([], [])), "structural"),
            _ => throw new ArgumentOutOfRangeException(nameof(refused)),
        };
        using MemoryStream stream = await ChunksAsync((context, payload));

        Eth2ReqRespException thrown = Assert.ThrowsAsync<Eth2ReqRespException>(() => new TestProtocol(Spec).ReadGloasAsync(stream, 1))!;
        Assert.That(thrown.Message, Does.Contain(reason));
    }

    [TestCase(1, false, null, TestName = "Post-BPO chunk under its own digest decodes")]
    [TestCase(1, true, "context bytes", TestName = "Post-BPO chunk under the Gloas fork epoch digest is refused")]
    [TestCase(2, false, "structural", TestName = "Post-BPO chunk over that epoch's blob max but under the frame bound is refused")]
    public async Task The_Gloas_reader_uses_the_blob_parameters_of_the_sidecars_own_epoch(int cells, bool underForkEpochDigest, string? refusal)
    {
        BeaconChainSpec spec = WithBlobEntry(Spec, new BlobScheduleEntry(Spec.GloasForkEpoch + 1, 1));
        ulong slot = (Spec.GloasForkEpoch + 1) * Spec.SlotsPerEpoch;
        DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(9, slot);
        (sidecar.Column, sidecar.KzgProofs) = (sidecar.Column![..cells], sidecar.KzgProofs![..cells]);
        byte[] context = ForkDigest.Compute(spec, underForkEpochDigest ? spec.GloasForkEpoch : spec.GetEpoch(slot));
        Assert.That(ForkDigest.Compute(spec, spec.GloasForkEpoch), Is.Not.EqualTo(ForkDigest.Compute(spec, spec.GetEpoch(slot))), "the BPO entry must change the digest");
        using MemoryStream stream = await ChunksAsync((context, DataColumnSidecarGloas.Encode(sidecar)));

        if (refusal is null)
        {
            Assert.That((await new TestProtocol(spec).ReadGloasAsync(stream, 1)).Select(static s => s.Slot), Is.EqualTo(new[] { slot }));
            return;
        }

        Eth2ReqRespException thrown = Assert.ThrowsAsync<Eth2ReqRespException>(() => new TestProtocol(spec).ReadGloasAsync(stream, 1))!;
        Assert.That(thrown.Message, Does.Contain(refusal));
    }

    [Test]
    public async Task The_Gloas_reader_accepts_a_sidecar_of_exactly_the_computed_bound()
    {
        const int maxBlobs = 64;
        BeaconChainSpec spec = WithBlobEntry(Spec, new BlobScheduleEntry(Spec.GloasForkEpoch + 1, maxBlobs));
        ulong slot = (Spec.GloasForkEpoch + 1) * Spec.SlotsPerEpoch;
        DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(9, slot);
        (sidecar.Column, sidecar.KzgProofs) = ([.. Enumerable.Repeat(sidecar.Column![0], maxBlobs)], [.. Enumerable.Repeat(sidecar.KzgProofs![0], maxBlobs)]);
        byte[] payload = DataColumnSidecarGloas.Encode(sidecar);
        Assert.That((ulong)payload.Length, Is.EqualTo(DataColumnSidecarGloasSize.ComputeMax(spec)));
        using MemoryStream stream = await ChunksAsync((ForkDigest.Compute(spec, spec.GetEpoch(slot)), payload));

        IReadOnlyList<DataColumnSidecarGloas> read = await new TestProtocol(spec).ReadGloasAsync(stream, 1);

        Assert.That(read.Select(static s => s.Column!.Length), Is.EqualTo(new[] { maxBlobs }));
    }

    [Test]
    public async Task The_Fulu_reader_refuses_a_Fulu_chunk_claiming_a_Gloas_slot_even_under_that_slots_digest()
    {
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(9, GloasStartSlot + 1, blobCount: 1);
        using MemoryStream stream = await ChunksAsync((ContextOf(GloasStartSlot + 1), DataColumnSidecar.Encode(sidecar)));

        Eth2ReqRespException thrown = Assert.ThrowsAsync<Eth2ReqRespException>(() => new TestProtocol(Spec).ReadFuluAsync(stream, 1))!;
        Assert.That(thrown.Message, Does.Contain("claims Gloas slot"));
    }

    [Test]
    public async Task The_Gloas_range_dial_returns_requested_sidecars([Values] bool windowEndsAtLastSlot)
    {
        ulong start = windowEndsAtLastSlot ? ulong.MaxValue - 1 : GloasStartSlot + 10;
        DataColumnSidecarGloas[] served = [DataColumnSidecarGloasTestFixture.BuildSidecar(3, start), DataColumnSidecarGloasTestFixture.BuildSidecar(7, start + 1)];

        IReadOnlyList<DataColumnSidecarGloas> read = await DialRangeAsync(new() { StartSlot = start, Count = 2, Columns = [3, 7] }, served);

        Assert.That(read.Select(static s => (s.Slot, s.Index)), Is.EqualTo(new[] { (start, 3UL), (start + 1, 7UL) }));
    }

    [TestCase(ulong.MaxValue, 3UL, TestName = "Slot before the window")]
    [TestCase(1UL, 3UL, TestName = "Slot past the window")]
    [TestCase(0UL, 7UL, TestName = "Unrequested column")]
    public void The_Gloas_range_dial_refuses_a_sidecar_outside_the_request(ulong slotOffset, ulong column)
    {
        ulong start = GloasStartSlot + 10;
        DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(column, unchecked(start + slotOffset));

        Eth2ReqRespException thrown = Assert.ThrowsAsync<Eth2ReqRespException>(() => DialRangeAsync(new() { StartSlot = start, Count = 1, Columns = [3] }, sidecar))!;
        Assert.That(thrown.Message, Does.Contain("outside the requested range or columns"));
    }

    [TestCase(2UL, 2, TestName = "More chunks than count times columns")]
    [TestCase(5000UL, 128, TestName = "More chunks than MAX_REQUEST_BLOCKS_DENEB slots times columns")]
    public void The_Gloas_range_dial_refuses_more_chunks_than_the_request_allows(ulong count, int allowed)
    {
        ulong start = GloasStartSlot + 10;
        DataColumnSidecarGloas[] served = [.. Enumerable.Range(0, allowed + 1).Select(i => DataColumnSidecarGloasTestFixture.BuildSidecar(3, start + (ulong)i % count))];

        Eth2ReqRespException thrown = Assert.ThrowsAsync<Eth2ReqRespException>(() => DialRangeAsync(new() { StartSlot = start, Count = count, Columns = [3] }, served))!;
        Assert.That(thrown.Message, Does.Contain($"more than the requested {allowed} "));
    }

    [TestCase(-1L, 2UL, TestName = "Window starting before the Gloas fork")]
    [TestCase(0L, 0UL, TestName = "Empty window")]
    [TestCase(long.MinValue, 3UL, TestName = "Window past the last slot")]
    public void The_Gloas_range_dial_refuses_a_window_not_wholly_in_Gloas_before_writing_to_the_wire(long startOffset, ulong count)
    {
        ulong start = startOffset == long.MinValue ? ulong.MaxValue - 1 : (ulong)((long)GloasStartSlot + startOffset);
        DataColumnSidecarsByRangeProtocol protocol = new(Spec, new DataColumnSidecarPool(), null!);

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => protocol.DialAsync(null!, null!, new(new DataColumnSidecarsByRangeRequest { StartSlot = start, Count = count, Columns = [3] }, Gloas: true)));
    }

    [Test]
    public void The_Gloas_root_dial_refuses_a_repeated_root_and_column()
    {
        Hash256 root = DataColumnSidecarGloasTestFixture.BlockRoot;
        DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(3, GloasStartSlot, root);

        Eth2ReqRespException thrown = Assert.ThrowsAsync<Eth2ReqRespException>(() =>
            DialRootAsync([new DataColumnsByRootIdentifier { BlockRoot = root, Columns = [3, 7] }], sidecar, sidecar))!;
        Assert.That(thrown.Message, Does.Contain("not requested"));
    }

    [Test]
    public void The_Gloas_root_dial_stops_reading_at_the_requested_column_count()
    {
        Hash256 root = DataColumnSidecarGloasTestFixture.BlockRoot;

        Eth2ReqRespException thrown = Assert.ThrowsAsync<Eth2ReqRespException>(() =>
            DialRootAsync([new DataColumnsByRootIdentifier { BlockRoot = root, Columns = [3, 7] }],
                DataColumnSidecarGloasTestFixture.BuildSidecar(3, GloasStartSlot, root), DataColumnSidecarGloasTestFixture.BuildSidecar(7, GloasStartSlot, root),
                DataColumnSidecarGloasTestFixture.BuildSidecar(9, GloasStartSlot, root)))!;
        Assert.That(thrown.Message, Does.Contain("more than the requested 2 "));
    }

    [Test]
    public async Task The_Gloas_root_dial_accepts_the_columns_of_one_root_split_over_two_identifiers()
    {
        Hash256 root = DataColumnSidecarGloasTestFixture.BlockRoot;

        IReadOnlyList<DataColumnSidecarGloas> read = await DialRootAsync(
            [new DataColumnsByRootIdentifier { BlockRoot = root, Columns = [3] }, new DataColumnsByRootIdentifier { BlockRoot = root, Columns = [7] }],
            DataColumnSidecarGloasTestFixture.BuildSidecar(3, GloasStartSlot, root), DataColumnSidecarGloasTestFixture.BuildSidecar(7, GloasStartSlot, root));

        Assert.That(read.Select(static s => s.Index), Is.EqualTo(new[] { 3UL, 7UL }));
    }

    [Test]
    public void The_Gloas_root_dial_refuses_a_sidecar_for_an_unrequested_root()
    {
        DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(3, GloasStartSlot, Keccak.Compute("other block"));

        Eth2ReqRespException thrown = Assert.ThrowsAsync<Eth2ReqRespException>(() =>
            DialRootAsync([new DataColumnsByRootIdentifier { BlockRoot = DataColumnSidecarGloasTestFixture.BlockRoot, Columns = [3] }], sidecar))!;
        Assert.That(thrown.Message, Does.Contain("not requested"));
    }

    [Test]
    public async Task By_root_serves_verified_Gloas_sidecars_under_their_slots_digest_and_never_a_pending_candidate()
    {
        Hash256 heldRoot = Keccak.Compute("held block");
        Hash256 pendingRoot = Keccak.Compute("pending block");
        DataColumnSidecarPool pool = new();
        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(3, GloasStartSlot + 2, heldRoot));
        pool.AddPendingGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(3, GloasStartSlot + 2, pendingRoot), "gossip peer");
        DataColumnSidecarsByRootProtocol protocol = new(Spec, pool);
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new Nethermind.Libp2p.Core.State());

        Channel channel = new();
        Task listen = ListenThenCloseAsync(protocol, channel.Reverse, context);
        Stream stream = new ChannelStreamAdapter(channel);
        DataColumnsByRootIdentifier[] request = [new() { BlockRoot = pendingRoot, Columns = [3] }, new() { BlockRoot = heldRoot, Columns = [3] }];
        await ReqRespFraming.WriteRequestAsync(stream, DataColumnSidecarsByRootRequest.Encode(new DataColumnSidecarsByRootRequest { Identifiers = request }), default);
        await channel.WriteEofAsync();

        List<ResponseChunk> chunks = [];
        while (await ReqRespFraming.ReadResponseChunkAsync(stream, ReqRespFraming.ForkContextLength, ReqRespFraming.MaxPayloadSize, default) is { } chunk)
        {
            chunks.Add(chunk);
        }

        await listen;
        Assert.That(chunks, Has.Count.EqualTo(1), "the pending candidate is not served");
        DataColumnSidecarGloas.Decode(chunks[0].Payload, out DataColumnSidecarGloas served);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chunks[0].ContextBytes, Is.EqualTo(ContextOf(GloasStartSlot + 2)));
            Assert.That((served.BeaconBlockRoot, served.Index), Is.EqualTo((heldRoot, 3UL)));
        }
    }

    private static BeaconChainSpec WithBlobEntry(BeaconChainSpec spec, BlobScheduleEntry entry) => new()
    {
        SecondsPerSlot = spec.SecondsPerSlot,
        SlotsPerEpoch = spec.SlotsPerEpoch,
        GenesisTime = spec.GenesisTime,
        GenesisValidatorsRoot = spec.GenesisValidatorsRoot,
        Forks = spec.Forks,
        BlobSchedule = [.. spec.BlobSchedule, entry],
        ElectraForkEpoch = spec.ElectraForkEpoch,
        FuluForkEpoch = spec.FuluForkEpoch,
        MaxBlobsPerBlockElectra = spec.MaxBlobsPerBlockElectra,
        GloasForkEpoch = spec.GloasForkEpoch,
        GloasForkVersion = spec.GloasForkVersion,
        Bootnodes = spec.Bootnodes,
    };

    private static byte[] ContextOf(ulong slot) => ForkDigest.Compute(Spec, Spec.GetEpoch(slot));

    private static byte[] Encode(ulong slot, Action<DataColumnSidecarGloas>? tamper = null)
    {
        DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(9, slot);
        tamper?.Invoke(sidecar);
        return DataColumnSidecarGloas.Encode(sidecar);
    }

    private static async Task<MemoryStream> ChunksAsync(params (byte[] Context, byte[] Payload)[] chunks)
    {
        MemoryStream stream = new();
        foreach ((byte[] context, byte[] payload) in chunks)
        {
            await ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, context, payload, default);
        }

        stream.Position = 0;
        return stream;
    }

    private static async Task<IReadOnlyList<DataColumnSidecarGloas>> DialRangeAsync(DataColumnSidecarsByRangeRequest request, params DataColumnSidecarGloas[] served)
    {
        Channel channel = new();
        _ = ServeAsync(channel.Reverse, served);
        ForkedDataColumnSidecars read = await new DataColumnSidecarsByRangeProtocol(Spec, new DataColumnSidecarPool(), null!).DialAsync(channel, null!, new(request, Gloas: true));
        return read.Gloas;
    }

    private static async Task<IReadOnlyList<DataColumnSidecarGloas>> DialRootAsync(DataColumnsByRootIdentifier[] request, params DataColumnSidecarGloas[] served)
    {
        Channel channel = new();
        _ = ServeAsync(channel.Reverse, served);
        ForkedDataColumnSidecars read = await new DataColumnSidecarsByRootProtocol(Spec, new DataColumnSidecarPool()).DialAsync(channel, null!, new(request, Gloas: true));
        return read.Gloas;
    }

    // A peer that answers any request with exactly these chunks, each under its own slot's digest.
    private static async Task ServeAsync(IChannel channel, DataColumnSidecarGloas[] served)
    {
        Stream stream = new ChannelStreamAdapter(channel);
        await ReqRespFraming.ReadRequestAsync(stream, ReqRespFraming.MaxPayloadSize, default);
        foreach (DataColumnSidecarGloas sidecar in served)
        {
            await ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, ContextOf(sidecar.Slot), DataColumnSidecarGloas.Encode(sidecar), default);
        }

        await channel.WriteEofAsync();
    }

    // The libp2p host closes the response stream once the handler returns; the dial side reads until then.
    private static async Task ListenThenCloseAsync(DataColumnSidecarsByRootProtocol protocol, IChannel channel, ISessionContext context)
    {
        await protocol.ListenAsync(channel, context);
        await channel.WriteEofAsync();
    }

    /// <summary>Exposes the protected chunked-response readers for direct testing.</summary>
    private sealed class TestProtocol(BeaconChainSpec spec) : DataColumnSidecarsProtocolBase(spec)
    {
        private const string ProtocolId = "/test/gloas-data-column-sidecars/1";

        public Task<IReadOnlyList<DataColumnSidecarGloas>> ReadGloasAsync(Stream stream, int maxSidecars) =>
            ReadGloasSidecarChunksAsync(stream, maxSidecars, ProtocolId);

        public Task<IReadOnlyList<DataColumnSidecar>> ReadFuluAsync(Stream stream, int maxSidecars) =>
            ReadSidecarChunksAsync(stream, maxSidecars, ProtocolId);
    }
}
