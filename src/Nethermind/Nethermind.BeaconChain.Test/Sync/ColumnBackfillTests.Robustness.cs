// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.P2P.RangeSyncTests;
using static Nethermind.BeaconChain.Test.Sync.RangeSyncPeerSelectionTests;

namespace Nethermind.BeaconChain.Test.Sync;

[NonParallelizable]
public partial class ColumnBackfillTests
{
    private sealed class HistoryFixture : IAsyncDisposable
    {
        public BeaconChainSpec Spec = ImportableBlobBlock.FuluFromGenesis;
        public BeaconChainStore Store = null!;
        public DataColumnSidecarPool Pool = null!;
        public SlotClock Clock = null!;
        public ManualTimestamper Time = null!;
        public BeaconChainStatusHolder Status = null!;
        public BeaconDiscovery Discovery = null!;
        public ulong[] Sampled = [];
        public Dictionary<Hash256, ForkedSignedBeaconBlock> Blocks = [];
        public Dictionary<Hash256, DataColumnSidecar[]> Columns = [];
        public List<Hash256> ChainRoots = [];
        public ConcurrentDictionary<Hash256, byte> Withheld = [];
        public ulong AnchorSlot;
        public ulong HeadSlot;
        public ColumnBackfill Backfill = null!;
        public CancellationTokenSource? Cts;
        private Task _run = Task.CompletedTask;
        public ILogManager LogManager = LimboLogs.Instance;

        public static HistoryFixture Build(int[] blobSlots, ulong anchorSlot, FaultyColumnsDb? db = null)
        {
            HistoryFixture p = new() { AnchorSlot = anchorSlot, HeadSlot = anchorSlot + 1 };
            IColumnsDb<BeaconChainDbColumns> database = db is null ? new MemColumnsDb<BeaconChainDbColumns>() : db;
            p.Store = new BeaconChainStore(database, p.Spec);
            SignedBeaconBlock genesis = TestChain.CreateBlock(0, Hash256.Zero);
            Hash256 parent = SszRoots.HashTreeRoot(genesis.Message!);
            p.Add(parent, genesis, null);
            foreach (int slot in blobSlots.OrderBy(static s => s))
            {
                (SignedBeaconBlock block, Hash256 root, DataColumnSidecar[] columns) = ImportableBlobBlock.BlobBlockAt((ulong)slot, parent);
                p.Add(root, block, columns);
                parent = root;
            }

            SignedBeaconBlock anchor = TestChain.CreateBlock(anchorSlot, parent);
            Hash256 anchorRoot = SszRoots.HashTreeRoot(anchor.Message!);
            p.Add(anchorRoot, anchor, null);
            SignedBeaconBlock head = TestChain.CreateBlock(p.HeadSlot, anchorRoot);
            Hash256 headRoot = SszRoots.HashTreeRoot(head.Message!);
            p.Add(headRoot, head, null);

            p.Store.SetAnchor(anchorRoot, anchorSlot);
            p.Store.PutForkedBlock(anchorRoot, p.Blocks[anchorRoot]);
            p.Store.PutForkedBlock(headRoot, p.Blocks[headRoot]);
            p.Store.ApplyCanonicalIndexChanges([(anchorSlot, anchorRoot), (p.HeadSlot, headRoot)], p.HeadSlot);
            ManualTimestamper time = new(DateTimeOffset.FromUnixTimeSeconds((long)(p.Spec.GenesisTime + (p.HeadSlot + 1) * p.Spec.SecondsPerSlot)).UtcDateTime);
            p.Time = time;
            p.Clock = new SlotClock(p.Spec, time);
            p.Status = new BeaconChainStatusHolder(p.Spec, time) { CurrentStatus = new StatusMessageV2 { ForkDigest = new byte[4], FinalizedRoot = anchorRoot, HeadRoot = headRoot, HeadSlot = p.HeadSlot } };
            p.Discovery = new BeaconDiscovery(new BeaconChainConfig { Discv5Port = 0 }, p.Spec, p.Store, new FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
            p.Discovery.CreateDiscv5Services(IPAddress.Loopback);
            p.Sampled = [.. new DiscoveryNodeCustodySource(p.Discovery).Current!.SampledColumns];
            p.Pool = new DataColumnSidecarPool(store: p.Store, clock: p.Clock);
            p.Pool.SeedCompletelyServableFloor(p.HeadSlot);
            return p;
        }

        private void Add(Hash256 root, SignedBeaconBlock block, DataColumnSidecar[]? columns)
        {
            Blocks[root] = new ForkedSignedBeaconBlock.OfFulu(block);
            ChainRoots.Add(root);
            if (columns is not null)
            {
                Columns[root] = columns;
            }
        }

        public ForkedSignedBeaconBlock[] Range(ulong start, ulong count) => [.. ChainRoots.Select(r => Blocks[r]).Where(b => b.Slot >= start && b.Slot < start + count)];

        public StubPeer Honest(string id) => new(
            id,
            HeadSlot,
            Range,
            rootHandler: ids => [.. ids.Where(i => !Withheld.ContainsKey(i.BlockRoot!)).SelectMany(i => i.Columns!.Select(c => Columns[i.BlockRoot!][c]))],
            blockRootHandler: roots => [.. roots.Where(Blocks.ContainsKey).Select(r => Blocks[r])]);

        public Task Start(CancellationToken token, params IBeaconSyncPeer[] peers)
        {
            StubPool pool = new(peers);
            RangeSync rangeSync = new(pool, LogManager, Pool, Spec, Clock, Discovery);
            Backfill = new ColumnBackfill(Store, Pool, rangeSync, pool, Clock, Spec, Status, new DiscoveryNodeCustodySource(Discovery), LogManager)
            {
                RetryDelay = TimeSpan.FromMilliseconds(50),
                WindowPause = TimeSpan.FromMilliseconds(1),
                HeadPollDelay = TimeSpan.FromMilliseconds(10),
            };
            Cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            return _run = Backfill.RunAsync(Cts.Token);
        }

        public async Task UntilFloor(ulong floor, CancellationToken token)
        {
            while (Pool.EarliestCompletelyServableSlot != floor)
            {
                if (_run.IsCompleted) Assert.Fail($"Backfill ended before reaching floor {floor}");
                await Task.Delay(10, token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Cts is not null)
            {
                await Cts.CancelAsync();
                await _run.WaitAsync(TimeSpan.FromSeconds(10));
                Cts.Dispose();
            }

            await Discovery.DisposeAsync();
        }
    }

    private sealed class SlowPeer(StubPeer inner, TimeSpan delay) : IBeaconSyncPeer
    {
        private int _inFlight;
        public int MaxInFlight;
        public string Id => inner.Id;
        public ulong HeadSlot => inner.HeadSlot;
        public PeerColumnCustody Custody => inner.Custody;
        public Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRangeAsync(ulong s, ulong c, CancellationToken t) => inner.RequestBlocksByRangeAsync(s, c, t);
        public Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRootAsync(Hash256[] r, CancellationToken t) => inner.RequestBlocksByRootAsync(r, t);
        public Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRangeAsync(ulong s, ulong c, ulong[] col, CancellationToken t) => inner.RequestDataColumnSidecarsByRangeAsync(s, c, col, t);

        public async Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] ids, CancellationToken t)
        {
            int now = Interlocked.Increment(ref _inFlight);
            int seen;
            while ((seen = Volatile.Read(ref MaxInFlight)) < now && Interlocked.CompareExchange(ref MaxInFlight, now, seen) != seen)
            {
            }

            try
            {
                await Task.Delay(delay, t);
                return await inner.RequestDataColumnSidecarsByRootAsync(ids, t);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRangeAsync(ulong s, ulong c, ulong[] col, CancellationToken t) => inner.RequestGloasDataColumnSidecarsByRangeAsync(s, c, col, t);
        public Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] ids, CancellationToken t) => inner.RequestGloasDataColumnSidecarsByRootAsync(ids, t);
        public Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRangeAsync(ulong s, ulong c, CancellationToken t) => throw new NotSupportedException();
        public Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRootAsync(Hash256[] r, CancellationToken t) => throw new NotSupportedException();
        public void ReportFailure(PeerFailureReason reason, string? detail = null) => inner.ReportFailure(reason, detail);
    }

    private sealed class ThrowingPeer(string id, ulong head, Func<Exception> make, StubPeer? blocks = null) : IBeaconSyncPeer
    {
        public int Calls;
        public List<string?> Details = [];
        public string Id => id;
        public ulong HeadSlot => head;
        public PeerColumnCustody Custody => StubPeer.AllColumns;
        private Task<T> Throw<T>() { Interlocked.Increment(ref Calls); throw make(); }
        public Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRangeAsync(ulong s, ulong c, CancellationToken t) => blocks?.RequestBlocksByRangeAsync(s, c, t) ?? Throw<IReadOnlyList<ForkedSignedBeaconBlock>>();
        public Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRootAsync(Hash256[] r, CancellationToken t) => blocks?.RequestBlocksByRootAsync(r, t) ?? Throw<IReadOnlyList<ForkedSignedBeaconBlock>>();
        public Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRangeAsync(ulong s, ulong c, ulong[] col, CancellationToken t) => Throw<IReadOnlyList<DataColumnSidecar>>();
        public Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] ids, CancellationToken t) => Throw<IReadOnlyList<DataColumnSidecar>>();
        public Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRangeAsync(ulong s, ulong c, ulong[] col, CancellationToken t) => Throw<IReadOnlyList<DataColumnSidecarGloas>>();
        public Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] ids, CancellationToken t) => Throw<IReadOnlyList<DataColumnSidecarGloas>>();
        public Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRangeAsync(ulong s, ulong c, CancellationToken t) => Throw<IReadOnlyList<SignedExecutionPayloadEnvelope>>();
        public Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRootAsync(Hash256[] r, CancellationToken t) => Throw<IReadOnlyList<SignedExecutionPayloadEnvelope>>();
        public void ReportFailure(PeerFailureReason reason, string? detail = null) { lock (Details) Details.Add(detail); }
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task HistoryFixture_concurrent_by_root_requests_to_one_peer(CancellationToken token)
    {
        await using HistoryFixture p = HistoryFixture.Build([1, 2, 3, 4, 5, 6, 7, 8], 9);
        SlowPeer peer = new(p.Honest("slow"), TimeSpan.FromMilliseconds(200));
        Task run = p.Start(token, peer);
        await p.UntilFloor(0, token);
        await run.WaitAsync(token);
        Assert.That(peer.MaxInFlight, Is.EqualTo(1));
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task HistoryFixture_flooding_and_duplicating_range_and_root_replies(CancellationToken token)
    {
        await using HistoryFixture p = HistoryFixture.Build([2, 5], 7);
        SignedBeaconBlock forged = TestChain.CreateBlock(3, Keccak.Compute("elsewhere"));
        Hash256 forgedRoot = SszRoots.HashTreeRoot(forged.Message!);
        ForkedSignedBeaconBlock forgedBlock = new ForkedSignedBeaconBlock.OfFulu(forged);
        StubPeer flooder = new(
            "flooder",
            p.HeadSlot,
            (start, count) => [.. Enumerable.Repeat(p.Range(start, count), 10).SelectMany(static b => b), .. Enumerable.Repeat(forgedBlock, 2000), .. p.Range(0, 1000)],
            rootHandler: ids => [.. ids.SelectMany(i => i.Columns!.Select(c => p.Columns[i.BlockRoot!][c])), .. ids.SelectMany(i => i.Columns!.Select(c => p.Columns[i.BlockRoot!][c])), .. p.Columns.Values.First()],
            blockRootHandler: roots => [forgedBlock, .. roots.Where(p.Blocks.ContainsKey).Select(r => p.Blocks[r])]);
        StubPeer honest = p.Honest("honest");
        Task run = p.Start(token, flooder, honest);
        await p.UntilFloor(0, token);
        await run.WaitAsync(token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(p.Store.HasBlock(forgedRoot), Is.False);
            Assert.That(p.ChainRoots.Take(3).All(p.Store.HasBlock), Is.True);
            Assert.That(flooder.Reports, Does.Contain(PeerFailureReason.ProtocolViolation));
            Assert.That(honest.Requests, Is.GreaterThan(0), "oversized replies are rejected before any block is stored");
        }
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task HistoryFixture_throwing_peers_do_not_end_the_backfill_or_log_the_word([Values(0, 1, 2)] int kind, [Values] bool columnsOnly, CancellationToken token)
    {
        AllLevelsCapture log = new();
        await using HistoryFixture p = HistoryFixture.Build([2, 5], 7);
        p.LogManager = new OneLoggerLogManager(new ILogger(log));
        Func<Exception> make = kind switch
        {
            0 => static () => new Exception(),
            1 => static () => new OperationCanceledException(),
            _ => static () => new InvalidOperationException(),
        };
        ThrowingPeer bad = new("bad", 100, make, columnsOnly ? p.Honest("blocks") : null);
        StubPeer honest = p.Honest("honest");
        Task run = p.Start(token, bad, honest);
        await p.UntilFloor(0, token);
        await run.WaitAsync(token);
        string[] lines;
        lock (log.Lines)
        {
            lines = [.. log.Lines];
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.IsCompletedSuccessfully, Is.True);
            Assert.That(lines, Has.None.Contains("Exception"));
            Assert.That(bad.Calls, Is.GreaterThan(0));
            Assert.That(bad.Details, Has.None.Contains("Exception"));
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task HistoryFixture_stale_progress_without_blocks_is_not_claimed(CancellationToken token)
    {
        await using HistoryFixture p = HistoryFixture.Build([2, 5], 7);
        byte[] zero = new byte[sizeof(ulong)];
        p.Store.PutMetadata(ColumnBackfill.ProgressKey, zero);
        StubPeer mute = new("mute", p.HeadSlot, static (_, _) => []);
        Task run = p.Start(token, mute);
        await Task.Delay(500, token);
        Assert.That(p.Pool.EarliestCompletelyServableSlot, Is.GreaterThan(p.AnchorSlot - 1), "no block below the anchor is held");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task HistoryFixture_garbage_progress_is_ignored([Values(3, 8, 16)] int kind, CancellationToken token)
    {
        await using HistoryFixture p = HistoryFixture.Build([2, 5], 7);
        byte[] value = kind == 8 ? [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff] : new byte[kind];
        p.Store.PutMetadata(ColumnBackfill.ProgressKey, value);
        Task run = p.Start(token, p.Honest("honest"));
        await p.UntilFloor(0, token);
        await run.WaitAsync(token);
        Assert.That(p.Sampled.All(c => p.Store.HasDataColumnRecord(p.ChainRoots[1], c)), Is.True);
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task HistoryFixture_missing_across_windows_stops_the_floor_contiguously(CancellationToken token)
    {
        await using HistoryFixture p = HistoryFixture.Build([5, 20, 35], 40);
        Hash256 missingRoot = p.ChainRoots[2];
        p.Withheld.TryAdd(missingRoot, 0);
        StubPeer honest = p.Honest("honest");
        Task run = p.Start(token, honest);
        await p.UntilFloor(21, token);
        await Task.Delay(500, token);
        ulong stuck = p.Pool.EarliestCompletelyServableSlot;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stuck, Is.EqualTo(21UL));
            Assert.That(p.Backfill.CompleteFrom, Is.EqualTo(21UL));
        }

        p.Withheld.Clear();
        // The retry rotation advances once per slot (fulu/p2p-interface.md request rate limits).
        p.Time.Add(TimeSpan.FromSeconds(p.Spec.SecondsPerSlot));
        await p.UntilFloor(0, token);
        await run.WaitAsync(token);
        Assert.That(p.ChainRoots.Where(p.Columns.ContainsKey).All(r => p.Sampled.All(c => p.Store.HasDataColumnRecord(r, c))), Is.True);
        Assert.That(Enumerable.Range(0, 42).Count(s => p.Store.TryGetCanonicalRoot((ulong)s, out _)), Is.EqualTo(6));
        Assert.That(honest.RequestedRanges.All(r => r.Count <= RangeSync.DefaultBatchSize), Is.True);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Backfill_stops_at_the_wall_clock_retention_boundary(CancellationToken token)
    {
        await using HistoryFixture p = HistoryFixture.Build([5, 20, 35], 40);
        ulong epoch = Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests + 1;
        ManualTimestamper time = new(DateTimeOffset.FromUnixTimeSeconds((long)(p.Spec.GenesisTime + epoch * p.Spec.SlotsPerEpoch * p.Spec.SecondsPerSlot)).UtcDateTime);
        p.Clock = new SlotClock(p.Spec, time);
        p.Pool = new DataColumnSidecarPool(store: p.Store, clock: p.Clock);
        await p.Pool.SeedCompletelyServableFloor(p.HeadSlot, token);
        p.Status.CurrentStatus = new StatusMessageV2 { ForkDigest = new byte[4], FinalizedRoot = Hash256.Zero, HeadRoot = Hash256.Zero, HeadSlot = p.Clock.CurrentSlot - 1 };
        Task run = p.Start(token, p.Honest("honest"));
        await run.WaitAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(p.Backfill.CompleteFrom, Is.EqualTo(p.Spec.SlotsPerEpoch));
            Assert.That(p.Store.HasBlock(p.ChainRoots[1]), Is.False);
            Assert.That(p.Sampled.All(c => p.Store.HasDataColumnRecord(p.ChainRoots[3], c)), Is.True);
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Cached_verified_columns_are_persisted_again_after_a_write_failure(CancellationToken token)
    {
        FaultyColumnsDb db = new();
        await using HistoryFixture p = HistoryFixture.Build([2, 5], 7, db);
        db.FailWrites = true;
        Task run = p.Start(token, p.Honest("honest"));
        await p.UntilFloor(6, token);
        db.FailWrites = false;
        await p.UntilFloor(0, token);
        await run.WaitAsync(token);
        Assert.That(p.Columns.Keys.All(r => p.Sampled.All(c => p.Store.HasDataColumnRecord(r, c))), Is.True);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Block_requests_try_at_most_three_peers_per_attempt(CancellationToken token)
    {
        await using HistoryFixture p = HistoryFixture.Build([2, 5], 7);
        int ranges = 0;
        TaskCompletionSource<int> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubPeer[] peers = [.. Enumerable.Range(0, 4).Select(i => new StubPeer($"peer{i}", p.HeadSlot,
            (_, _) => { Interlocked.Increment(ref ranges); throw new InvalidOperationException("unavailable"); },
            blockRootHandler: _ => { observed.TrySetResult(ranges); p.Cts!.Cancel(); return []; }))];
        Task run = p.Start(token, peers);
        int attempted = await observed.Task.WaitAsync(token);
        await run.WaitAsync(token);
        Assert.That(attempted, Is.EqualTo(3));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Cancellation_stops_backfill_while_waiting_for_head(CancellationToken token)
    {
        await using HistoryFixture p = HistoryFixture.Build([2, 5], 7);
        p.Status.CurrentStatus = new StatusMessageV2 { ForkDigest = new byte[4], FinalizedRoot = Hash256.Zero, HeadRoot = Hash256.Zero, HeadSlot = 0 };
        Task run = p.Start(token, p.Honest("honest"));
        await p.Cts!.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(3), token);
        Assert.That(run.IsCompletedSuccessfully, Is.True);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task HistoryFixture_head_at_slack_boundary_counts_as_followed(CancellationToken token)
    {
        await using HistoryFixture p = HistoryFixture.Build([2, 5], 7);
        p.Status.CurrentStatus = new StatusMessageV2 { ForkDigest = new byte[4], FinalizedRoot = Hash256.Zero, HeadRoot = Hash256.Zero, HeadSlot = p.Clock.CurrentSlot - 2 };
        Task run = p.Start(token, p.Honest("honest"));
        await p.UntilFloor(0, token);
        await run.WaitAsync(token);
    }
}
