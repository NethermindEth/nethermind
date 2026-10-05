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

    private const ulong RangeSlot = 13_410_304;
    private sealed record RangeServingCase(string Name, ulong Count = 2, int StartOffset = 0,
        int HeldOffset = 0, ulong HeldCount = 2, ulong[]? Columns = null, ulong[]? HeldColumns = null,
        ulong? ClockSlot = null, (int Offset, ulong Column)[]? Expected = null, bool Unavailable = false,
        bool CheckFailureMetric = false, bool? CanonicalFirst = null, bool RepeatSidecar = false, bool Largest = false);

    private static ulong ServeRangeClock(ulong epochsBelow) =>
        (Spec.GetEpoch(RangeSlot) - epochsBelow + Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests) * Spec.SlotsPerEpoch;

    private static readonly RangeServingCase[] RangeServingScenarios =
    [
        new("Canonical sidecar wins when it arrived first", Count: 3, HeldCount: 3, CanonicalFirst: true, Expected: [(0, 5), (2, 5)]),
        new("Canonical sidecar wins when it arrived last", Count: 3, HeldCount: 3, CanonicalFirst: false, Expected: [(0, 5), (2, 5)]),
        new("Requested columns are unique and ordered by slot then column", Columns: [7, 9, 5, 5], HeldColumns: [5, 7], Expected: [(0, 5), (0, 7), (1, 5), (1, 7)]),
        new("Sidecars serve past the block request slot limit", Count: BlocksProtocolBase.MaxRequestBlocks + 1, HeldCount: BlocksProtocolBase.MaxRequestBlocks + 1, RepeatSidecar: true),
        new("An unbounded requested count still serves past the block limit", Count: ulong.MaxValue, HeldCount: BlocksProtocolBase.MaxRequestBlocks + 1, RepeatSidecar: true),
        new("A held sidecar beyond the slot walk is resource unavailable", Count: DataColumnSidecarsProtocolBase.MaxRequestDataColumnSidecars + 1, HeldOffset: (int)DataColumnSidecarsProtocolBase.MaxRequestDataColumnSidecars, HeldCount: 1, Unavailable: true),
        new("Inside serve range from one slot below complete columns", StartOffset: -1, ClockSlot: ServeRangeClock(1), Unavailable: true, CheckFailureMetric: true),
        new("Inside serve range at the single slot below complete columns", Count: 1, StartOffset: -1, ClockSlot: ServeRangeClock(1), Unavailable: true, CheckFailureMetric: true),
        new("Inside serve range from the first complete slot", ClockSlot: ServeRangeClock(1)),
        new("Inside serve range across slots below complete columns", Count: 5, StartOffset: -3, ClockSlot: ServeRangeClock(1), Unavailable: true, CheckFailureMetric: true),
        new("Wholly below the serve range", Count: 3, StartOffset: -3, ClockSlot: ServeRangeClock(0), Expected: []),
        new("Below serve range up to complete columns", Count: 5, StartOffset: -3, ClockSlot: ServeRangeClock(0)),
        new("Ending one slot below serve range", Count: 3, StartOffset: -35, ClockSlot: ServeRangeClock(1), Expected: []),
        new("Held columns below serve range remain served", StartOffset: -1, HeldOffset: -1, ClockSlot: ServeRangeClock(0), Expected: [(-1, 5), (0, 5)]),
        new("Future request is empty with no held columns", Count: 4, StartOffset: 1, HeldCount: 0, ClockSlot: RangeSlot, Expected: []),
        new("Future request is empty even with held future columns", Count: 4, StartOffset: 1, HeldOffset: 1, HeldCount: 1, ClockSlot: RangeSlot, Expected: []),
        new("Largest incompressible sidecar round trips whole", Count: 1, HeldCount: 1, Columns: [100], HeldColumns: [100], Expected: [(0, 100)], Largest: true),
    ];

    private static IEnumerable<TestCaseData> RangeServingCases()
    {
        for (int i = 0; i < RangeServingScenarios.Length; i++)
            yield return new TestCaseData(i).SetName(RangeServingScenarios[i].Name);
    }

    [TestCaseSource(nameof(RangeServingCases))]
    public async Task Range_serving_preserves_canonical_order_limits_and_availability(int index)
    {
        RangeServingCase test = RangeServingScenarios[index];
        DataColumnSidecarPool pool = new();
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        DataColumnSidecar? repeated = test.RepeatSidecar ? DataColumnSidecarTestFixture.BuildValidSidecar(5, RangeSlot, blobCount: 1) : null;
        byte[]? largestSsz = null;
        for (ulong i = 0; i < test.HeldCount; i++)
        {
            ulong slot = (ulong)((long)RangeSlot + test.HeldOffset) + i;
            Hash256 root = Keccak.Compute($"canonical {slot}");
            foreach (ulong column in test.HeldColumns ?? [5UL])
            {
                DataColumnSidecar canonical = repeated ?? DataColumnSidecarTestFixture.BuildValidSidecar(column, slot,
                    proposerIndex: test.CanonicalFirst is not null ? 1UL : 0UL,
                    blobCount: test.Largest ? (int)Spec.GetBlobParameters(Spec.GetEpoch(slot))!.Value.MaxBlobsPerBlock : 1,
                    seed: test.Largest ? (byte)1 : test.CanonicalFirst is not null ? (byte)0x21 : (byte)0x10);
                if (test.CanonicalFirst is { } first)
                {
                    bool canonicalFirst = i == 2 ? !first : first;
                    Hash256 competingRoot = Keccak.Compute($"competing {slot}");
                    DataColumnSidecar competing = DataColumnSidecarTestFixture.BuildValidSidecar(column, slot, 2, blobCount: 1, seed: 0x22);
                    if (canonicalFirst) pool.Add(root, slot, canonical);
                    pool.Add(competingRoot, slot, competing);
                    if (!canonicalFirst) pool.Add(root, slot, canonical);
                }
                else pool.Add(root, slot, canonical);
                if (test.Largest)
                {
                    largestSsz = DataColumnSidecar.Encode(canonical);
                    Assert.That(Snappy.CompressToArray(largestSsz), Has.Length.GreaterThan(largestSsz.Length * 99 / 100), "fixture: cells must be incompressible to approach the wire bound");
                }
            }
            if (test.CanonicalFirst is null || i != 1) store.SetCanonicalRoot(slot, root);
        }
        SlotClock? clock = test.ClockSlot is { } now ? new(Spec, new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)(Spec.GenesisTime + now * Spec.SecondsPerSlot)).UtcDateTime)) : null;
        long invalidBefore = FailureCount(ByRangeId, ReqRespFailureReason.InvalidMessage);
        Task<IReadOnlyList<DataColumnSidecar>> request = RequestRangeAsync(pool, store, (ulong)((long)RangeSlot + test.StartOffset), test.Count, test.Columns ?? [5UL], clock);
        if (test.Unavailable)
        {
            Eth2ReqRespException? refused = Assert.ThrowsAsync<Eth2ReqRespException>(async () => await request);
            Assert.That(refused!.ResponseCode, Is.EqualTo(ReqRespFraming.ResponseCode.ResourceUnavailable));
            if (test.CheckFailureMetric) Assert.That(FailureCount(ByRangeId, ReqRespFailureReason.InvalidMessage), Is.EqualTo(invalidBefore), "an honest request for missing columns is not the requester's fault");
        }
        else
        {
            IReadOnlyList<DataColumnSidecar> served = await request;
            if (test.RepeatSidecar) Assert.That(served, Has.Count.EqualTo(test.HeldCount));
            else Assert.That(served.Select(static s => Key(s)), Is.EqualTo((test.Expected ?? [(0, 5UL), (1, 5UL)]).Select(e => ((ulong)((long)RangeSlot + e.Offset), e.Column))));
            if (test.CanonicalFirst is not null) Assert.That(served.Select(static s => s.SignedBlockHeader!.Message!.ProposerIndex), Is.All.EqualTo(1));
            if (test.Largest) Assert.That(served.Select(static s => DataColumnSidecar.Encode(s)), Is.EqualTo(new[] { largestSsz! }));
        }
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

    [Test]
    [CancelAfter(30_000)]
    public async Task The_real_dial_hands_out_each_chunk_read_before_the_reply_is_cut_short(CancellationToken token)
    {
        DataColumnSidecar[] good = [.. Enumerable.Range(0, 3).Select(i => DataColumnSidecarTestFixture.BuildValidSidecar(i % 2 == 0 ? 3UL : 4UL, slot: 100 + (ulong)i))];
        byte[] cut = await EncodeChunkAsync(DataColumnSidecarTestFixture.BuildValidSidecar(3, slot: 103));
        List<DataColumnSidecar> seen = [];

        Eth2ReqRespException? thrown = Assert.ThrowsAsync<Eth2ReqRespException>(() => DialRangeAsync(good, cut[..(cut.Length / 2)], seen.Add, token: token));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(seen.Select(Key), Is.EqualTo(good.Select(Key)), "a chunk fully read is kept when a later chunk is truncated");
        Assert.That(thrown!.Message, Does.StartWith("Truncated response chunk"));
    }

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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(seen, Has.Count.EqualTo(2), "the third chunk was never read");
        Assert.That(cut!.Message, Is.EqualTo("timed out after 3.5 s, the bound for the whole response, with 2 chunks read"));
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
