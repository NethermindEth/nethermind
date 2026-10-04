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
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.Types;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.P2P.RangeSyncTests;
using static Nethermind.BeaconChain.Test.Sync.RangeSyncPeerSelectionTests;

namespace Nethermind.BeaconChain.Test.Sync;

public partial class ColumnBackfillTests
{
    private const ulong AnchorSlot = 4;
    private const ulong HeadSlot = 5;

    [Test]
    [CancelAfter(60_000)]
    public async Task Blocks_and_sampled_columns_below_the_anchor_are_stored_and_the_floor_reaches_the_window_start(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer peer = fixture.Peer("peer");

        Task run = fixture.StartBackfill(token, peer);
        await fixture.UntilFloorAsync(0, token);
        await run.WaitAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Enumerable.Range(0, 6).Select(slot => fixture.Store.TryGetCanonicalRoot((ulong)slot, out Hash256? root) ? root : null), Is.EqualTo(fixture.Roots), "every canonical block below the anchor is indexed");
            Assert.That(fixture.Roots.All(fixture.Store.HasBlock), Is.True, "and stored");
            foreach (Hash256 blobRoot in fixture.BlobRoots)
            {
                Assert.That(fixture.Sampled.All(column => fixture.Store.HasDataColumnRecord(blobRoot, column)), Is.True, "every sampled column of a blob block is stored");
            }

            Assert.That(fixture.Sampled.Any(column => fixture.Store.HasDataColumnRecord(fixture.Roots[2], column)), Is.False, "a block without blobs has no columns");
            Assert.That(peer.RequestedColumns.SelectMany(static c => c).Distinct(), Is.EquivalentTo(fixture.Sampled), "only the sampled columns are asked for");
            Assert.That(fixture.Backfill.CompleteFrom, Is.EqualTo(0UL));
            Assert.That(fixture.Store.BackfilledBlockFloor, Is.EqualTo(0UL));
            Assert.That(fixture.Store.GetMetadata(ColumnBackfill.ProgressKey), Is.EqualTo(new byte[sizeof(ulong)]), "the stored progress is the floor");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_slot_no_peer_can_serve_stops_the_floor_above_it_and_is_asked_for_again_later(CancellationToken token)
    {
        AllLevelsCapture log = new();
        await using Fixture fixture = Fixture.Create(new OneLoggerLogManager(new ILogger(log)));
        fixture.WithheldRoots.TryAdd(fixture.Roots[1], 0);
        StubPeer peer = fixture.Peer("peer");

        Task run = fixture.StartBackfill(token, peer);
        await fixture.UntilFloorAsync(2, token);
        int requestsAtMissing = peer.RootColumnRequests;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Pool.EarliestCompletelyServableSlot, Is.EqualTo(2UL), "the floor stops above the slot with a missing column, not at the window start");
            Assert.That(fixture.Sampled.Any(column => fixture.Store.HasDataColumnRecord(fixture.Roots[1], column)), Is.False, "nothing is claimed for the slot the peer could not serve");
            Assert.That(fixture.Sampled.All(column => fixture.Store.HasDataColumnRecord(fixture.Roots[3], column)), Is.True, "the columns above the missing are held");
            Assert.That(run.IsCompleted, Is.False, "the backfill goes on trying");
        }

        fixture.WithheldRoots.Clear();
        fixture.AdvanceSlots(1);
        await fixture.UntilFloorAsync(0, token);
        await run.WaitAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.RootColumnRequests, Is.GreaterThan(requestsAtMissing), "a later round asks again");
            Assert.That(fixture.Sampled.All(column => fixture.Store.HasDataColumnRecord(fixture.Roots[1], column)), Is.True);
            Assert.That(log.Lines, Has.Some.Contains("cannot complete slot 1"));
            Assert.That(log.Lines, Has.None.Contains("Exception"), "the sync gate fails a job on any log line with that word");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_blocked_slot_is_routine_at_debug_and_a_warning_once_it_stays_blocked_naming_its_columns_and_the_custodians_asked(CancellationToken token)
    {
        LevelCapturingLogManager logs = new();
        await using Fixture fixture = Fixture.Create(logs);
        fixture.WithheldRoots.TryAdd(fixture.Roots[1], 0);

        Task run = fixture.StartBackfill(token, fixture.Peer("peer"));
        while (!Blocked().Any(static line => line.Level == "Warn"))
        {
            await Task.Delay(10, token);
        }

        await fixture.StopAsync(run);
        (string Level, string Text)[] blocked = Blocked();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocked.Take(ColumnBackfill.BlockedAttemptsBeforeWarning - 1).Select(static line => line.Level), Is.All.EqualTo("Debug"), "a retry is routine until the slot stays blocked");
            Assert.That(blocked[ColumnBackfill.BlockedAttemptsBeforeWarning - 1].Level, Is.EqualTo("Warn"));
            Assert.That(blocked[ColumnBackfill.BlockedAttemptsBeforeWarning - 1].Text, Does.StartWith(
                $"Data column backfill cannot complete slot 1 yet (attempt {ColumnBackfill.BlockedAttemptsBeforeWarning}): columns {string.Join(", ", fixture.Sampled)} are missing after asking 1 custodian;"));
        }

        (string Level, string Text)[] Blocked() => [.. logs.Lines.Where(static line => line.Text.Contains("cannot complete slot"))];
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_restart_lowers_the_floor_to_the_stored_progress_before_the_head_is_followed(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        Task first = fixture.StartBackfill(token, fixture.Peer("peer"));
        await fixture.UntilFloorAsync(0, token);
        await first.WaitAsync(token);

        Fixture restarted = fixture.Restart();
        restarted.SetHead(1);
        Task run = restarted.StartBackfill(token, restarted.Peer("peer"));
        await restarted.UntilFloorAsync(0, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(restarted.Peers.Sum(static p => p.Requests + p.RootBlockRequests + p.RootColumnRequests), Is.Zero, "the stored progress is confirmed against the stored columns, not fetched again");
            Assert.That(restarted.Backfill.CompleteFrom, Is.EqualTo(0UL));
        }

        await restarted.StopAsync(run);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_restart_after_a_missing_asks_only_for_the_columns_still_missing(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        fixture.WithheldRoots.TryAdd(fixture.Roots[1], 0);
        Task first = fixture.StartBackfill(token, fixture.Peer("peer"));
        await fixture.UntilFloorAsync(2, token);
        await fixture.StopAsync(first);

        Fixture restarted = fixture.Restart();
        StubPeer peer = restarted.Peer("peer");
        Task run = restarted.StartBackfill(token, peer);
        await restarted.UntilFloorAsync(0, token);
        await run.WaitAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.Requests + peer.RootBlockRequests, Is.Zero, "the blocks are stored");
            Assert.That(restarted.RequestedRoots, Is.All.EqualTo(restarted.Roots[1]), "only the incomplete block's columns are asked for");
            Assert.That(restarted.RequestedRoots, Is.Not.Empty);
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Nothing_is_requested_while_the_head_is_behind_and_the_backfill_starts_when_it_is_followed(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        fixture.SetHead(1);
        StubPeer peer = fixture.Peer("peer");

        Task run = fixture.StartBackfill(token, peer);
        await Task.Delay(300, token);
        int requestsWhileBehind = peer.Requests + peer.RootBlockRequests + peer.RootColumnRequests;
        ulong floorWhileBehind = fixture.Pool.EarliestCompletelyServableSlot;

        fixture.SetHead(HeadSlot);
        await fixture.UntilFloorAsync(0, token);
        await run.WaitAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requestsWhileBehind, Is.Zero);
            Assert.That(floorWhileBehind, Is.EqualTo(ulong.MaxValue), "and no floor is claimed");
            Assert.That(peer.Requests + peer.RootBlockRequests + peer.RootColumnRequests, Is.GreaterThan(0));
        }
    }

    public enum BackfillReply
    {
        LinkedPartial,
        ForgedPartial,
        TwoPartials,
        OmittedBlock,
        UnlinkedUpper,
        ForgedWhole,
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Backfill_preserves_linked_ranges_and_blames_only_proven_reply_faults([Values] BackfillReply reply, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        bool forged = reply is BackfillReply.ForgedPartial or BackfillReply.ForgedWhole;
        SignedBeaconBlock invalid = TestChain.CreateBlock(reply == BackfillReply.ForgedWhole ? 3UL : 2UL, Hash256.Zero);
        Hash256 invalidRoot = SszRoots.HashTreeRoot(invalid.Message!);
        StubPeer? source = forged ? null : fixture.Peer("source");
        StubPeer first = new("first", HeadSlot, (start, _) =>
        {
            ForkedSignedBeaconBlock[] blocks = forged
                ? [new ForkedSignedBeaconBlock.OfFulu(invalid)]
                : [.. source!.RequestBlocksByRangeAsync(start, 2, token).Result];
            return reply == BackfillReply.ForgedWhole ? blocks : throw new PartialBlocksException(new TimeoutException("request timed out"), blocks);
        }, blockRootHandler: reply == BackfillReply.ForgedWhole ? _ => [new ForkedSignedBeaconBlock.OfFulu(invalid)] : null);
        StubPeer second = reply switch
        {
            BackfillReply.TwoPartials => new("second", HeadSlot, (start, _) => throw new PartialBlocksException(new TimeoutException("request timed out"), [.. source!.RequestBlocksByRangeAsync(start, 1, token).Result])),
            BackfillReply.OmittedBlock => new("second", HeadSlot, (_, _) => [.. source!.RequestBlocksByRangeAsync(3, 1, token).Result]),
            BackfillReply.UnlinkedUpper => new("second", HeadSlot, (start, _) => [.. source!.RequestBlocksByRangeAsync(start, 1, token).Result]),
            _ => fixture.Peer("honest"),
        };
        bool thirdNeeded = reply is not (BackfillReply.LinkedPartial or BackfillReply.ForgedWhole);
        StubPeer? third = thirdNeeded ? fixture.Peer("next") : null;
        StubPeer[] peers = third is null ? [first, second] : [first, second, third];
        Task run = fixture.StartBackfill(token, peers);
        await fixture.UntilFloorAsync(0, token);
        await run.WaitAsync(token);

        using (Assert.EnterMultipleScope())
        {
            if (reply == BackfillReply.ForgedWhole)
            {
                Assert.That(first.Reports, Does.Contain(PeerFailureReason.ProtocolViolation));
                Assert.That(fixture.Store.TryGetCanonicalRoot(3, out Hash256? canonical) ? canonical : null, Is.EqualTo(fixture.Roots[3]));
            }
            else
            {
                Assert.That(first.Reports, Is.EqualTo(reply == BackfillReply.ForgedPartial
                    ? new[] { PeerFailureReason.RequestFailed, PeerFailureReason.ProtocolViolation }
                    : new[] { PeerFailureReason.RequestFailed }));
                Assert.That(peers.Sum(peer => peer.RootBlockRequests), Is.Zero, "linked range replies require no by-root fetch");
                Assert.That(fixture.Roots[..4].All(fixture.Store.HasBlock), Is.True);
            }
            if (forged) Assert.That(fixture.Store.HasBlock(invalidRoot), Is.False);
            switch (reply)
            {
                case BackfillReply.LinkedPartial:
                    Assert.That(first.RequestedRanges, Is.EqualTo(new[] { (0UL, AnchorSlot) }));
                    Assert.That(second.RequestedRanges, Is.EqualTo(new[] { (2UL, AnchorSlot - 2) }));
                    Assert.That(second.Reports, Is.Empty);
                    break;
                case BackfillReply.ForgedPartial:
                    Assert.That(second.RequestedRanges[0], Is.EqualTo((3UL, 1UL)));
                    Assert.That(third!.RequestedRanges[0], Is.EqualTo((0UL, 3UL)));
                    Assert.That(second.Reports.Concat(third.Reports), Is.Empty);
                    break;
                case BackfillReply.TwoPartials:
                case BackfillReply.OmittedBlock:
                    Assert.That(second.Reports, Is.EqualTo(new[] { reply == BackfillReply.OmittedBlock ? PeerFailureReason.ProtocolViolation : PeerFailureReason.RequestFailed }));
                    Assert.That(third!.Reports, Is.Empty);
                    break;
                case BackfillReply.UnlinkedUpper:
                    Assert.That(second.RequestedRanges[0], Is.EqualTo((2UL, 2UL)));
                    Assert.That(third!.RequestedRanges[0], Is.EqualTo((2UL, 2UL)), "kept slots are not requested again");
                    break;
            }
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_Gloas_block_has_its_columns_fetched_only_when_its_payload_was_revealed([Values] bool revealed, CancellationToken token)
    {
        BeaconChainSpec spec = RangeSyncGloasColumnsTests.Spec;
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), spec);
        const ulong currentSlot = 34;
        ManualTimestamper time = new(DateTimeOffset.FromUnixTimeSeconds((long)(spec.GenesisTime + currentSlot * spec.SecondsPerSlot)).UtcDateTime);
        SlotClock clock = new(spec, time);
        SignedBeaconBlock genesis = TestChain.CreateBlock(0, Hash256.Zero);
        SignedBeaconBlock fulu = TestChain.CreateBlock(31, SszRoots.HashTreeRoot(genesis.Message!));
        SignedBeaconBlockGloas revealedOrNot = SignedBeaconBlockBuilders.CreateMinimalGloasBlock(32, SszRoots.HashTreeRoot(fulu.Message!));
        ExecutionPayloadBid bid = revealedOrNot.Message!.Body!.SignedExecutionPayloadBid!.Message!;
        bid.BlobKzgCommitments = DataColumnSidecarGloasTestFixture.Commitments();
        bid.BlockHash = Keccak.Compute("payload");
        Hash256 gloasRoot = SszRoots.HashTreeRoot(revealedOrNot.Message);
        SignedBeaconBlockGloas child = SignedBeaconBlockBuilders.CreateMinimalGloasBlock(33, gloasRoot);
        child.Message!.Body!.SignedExecutionPayloadBid!.Message!.ParentBlockHash = revealed ? bid.BlockHash : Keccak.Compute("another payload");
        Hash256 childRoot = SszRoots.HashTreeRoot(child.Message);
        ForkedSignedBeaconBlock[] chain = [new ForkedSignedBeaconBlock.OfFulu(genesis), new ForkedSignedBeaconBlock.OfFulu(fulu), new ForkedSignedBeaconBlock.OfGloas(revealedOrNot), new ForkedSignedBeaconBlock.OfGloas(child)];
        store.SetAnchor(childRoot, child.Message.Slot);
        store.PutForkedBlock(childRoot, chain[3]);
        store.ApplyCanonicalIndexChanges([(child.Message.Slot, childRoot)], child.Message.Slot);
        DataColumnSidecarPool pool = new(store: store);
        await using BeaconDiscovery discovery = new(new BeaconChainConfig { Discv5Port = 0 }, spec, store, new FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
        discovery.CreateDiscv5Services(IPAddress.Loopback);
        ulong[] sampled = [.. new DiscoveryNodeCustodySource(discovery).Current!.SampledColumns];
        int gloasRequests = 0;
        StubPeer peer = new(
            "peer",
            child.Message.Slot,
            (start, count) => [.. chain.Where(b => b.Slot >= start && b.Slot - start < count)],
            gloasRootHandler: identifiers =>
            {
                Interlocked.Increment(ref gloasRequests);
                return [.. identifiers.SelectMany(i => i.Columns!.Select(column => DataColumnSidecarGloasTestFixture.BuildSidecar(column, 32, gloasRoot)))];
            },
            blockRootHandler: roots => [.. chain.Where(b => roots.Contains(b.ComputeMessageRoot()))]);
        StubPool peers = new(peer);
        BeaconChainStatusHolder status = new(spec, time) { CurrentStatus = new StatusMessageV2 { ForkDigest = new byte[4], FinalizedRoot = childRoot, HeadRoot = childRoot, HeadSlot = child.Message.Slot } };
        ColumnBackfill backfill = new(store, pool, new RangeSync(peers, LimboLogs.Instance, pool, spec, clock, discovery), peers, clock, spec, status, new DiscoveryNodeCustodySource(discovery), LimboLogs.Instance)
        {
            RetryDelay = TimeSpan.FromMilliseconds(50),
            WindowPause = TimeSpan.FromMilliseconds(1),
            HeadPollDelay = TimeSpan.FromMilliseconds(10),
        };

        await backfill.RunAsync(token).WaitAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(backfill.CompleteFrom, Is.EqualTo(0UL), "the backfill is complete either way");
            Assert.That(sampled.All(column => store.HasDataColumnRecord(gloasRoot, column)), Is.EqualTo(revealed));
            Assert.That(gloasRequests > 0, Is.EqualTo(revealed), "no peer is asked for the columns of a block whose payload was not revealed");
        }
    }

    /// <summary>
    /// Fetched columns reach the store through the pool's writer thread, so the check that a window is complete must wait for the
    /// queued writes; otherwise nearly every window with blobs would count as incomplete and wait for the retry.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task A_window_whose_fetched_columns_are_still_queued_for_the_store_completes_without_a_retry(CancellationToken token)
    {
        using ColumnStoreWriter writer = new(LimboLogs.Instance);
        await using Fixture fixture = Fixture.Create(storeWriter: writer);
        using ManualResetEventSlim release = new();
        int held = 0;
        // Held from the first column request, so the fetched columns' writes queue behind it while the fetch returns.
        fixture.OnColumnsRequested = () =>
        {
            if (Interlocked.Exchange(ref held, 1) == 0)
            {
                writer.Post(() => release.Wait(token));
                _ = Task.Delay(300, token).ContinueWith(_ => release.Set(), TaskScheduler.Default);
            }
        };

        Task run = fixture.StartBackfill(TimeSpan.FromHours(1), token, fixture.Peer("peer"));

        Assert.That(async () => await fixture.UntilFloorAsync(0, token).WaitAsync(TimeSpan.FromSeconds(10), token), Throws.Nothing, "the window waited for the retry");
        await run.WaitAsync(token);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Completed_backfill_serves_blocks_below_the_anchor_by_range(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        await fixture.StartBackfill(token, fixture.Peer("peer")).WaitAsync(token);
        BeaconBlocksByRangeProtocolV2 protocol = new(ImportableBlobBlock.FuluFromGenesis, fixture.Store);

        IReadOnlyList<ForkedSignedBeaconBlock> served = await CanonicalIndexReorgServingTests.ServeAsync(
            protocol, (channel, context) => protocol.DialAsync(channel, context, new BeaconBlocksByRangeDial(new BeaconBlocksByRangeRequest { StartSlot = 0, Count = 6, Step = 1 })));

        Assert.That(served.Select(static b => b.ComputeMessageRoot()), Is.EqualTo(fixture.Roots));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ImportableBlobBlock _chain;
        private readonly ManualTimestamper _time;
        private readonly BeaconDiscovery _discovery;
        private readonly ILogManager? _givenLogManager;
        private readonly List<Fixture> _restarts = [];
        private readonly Dictionary<Hash256, DataColumnSidecar[]> _columnsByRoot = [];
        private readonly Dictionary<Hash256, ForkedSignedBeaconBlock> _blocksByRoot = [];
        private readonly List<StubPeer> _peers = [];
        private CancellationTokenSource? _cts;
        private Task _run = Task.CompletedTask;
        private Task _floorCheck = Task.CompletedTask;

        private Fixture(ImportableBlobBlock chain, BeaconChainStore store, ManualTimestamper time, ILogManager? logManager)
        {
            _chain = chain;
            Store = store;
            _time = time;
            _givenLogManager = logManager;
            Clock = new SlotClock(chain.Spec, time);
            Status = new BeaconChainStatusHolder(chain.Spec, time);
            _discovery = new BeaconDiscovery(new BeaconChainConfig { Discv5Port = 0 }, chain.Spec, store, new FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
            _discovery.CreateDiscv5Services(IPAddress.Loopback);
            Sampled = [.. new DiscoveryNodeCustodySource(_discovery).Current!.SampledColumns];
            LogManager = logManager ?? LimboLogs.Instance;

            SignedBeaconBlock slot2 = TestChain.CreateBlock(2, chain.BlockRoot);
            Hash256 root2 = SszRoots.HashTreeRoot(slot2.Message!);
            (SignedBeaconBlock slot3, Hash256 root3, DataColumnSidecar[] columns3) = ImportableBlobBlock.BlobBlockAt(3, root2);
            SignedBeaconBlock slot4 = TestChain.CreateBlock(AnchorSlot, root3);
            Hash256 root4 = SszRoots.HashTreeRoot(slot4.Message!);
            SignedBeaconBlock slot5 = TestChain.CreateBlock(HeadSlot, root4);
            Hash256 root5 = SszRoots.HashTreeRoot(slot5.Message!);
            SignedBeaconBlock[] blocks = [chain.AnchorBlock, chain.Block, slot2, slot3, slot4, slot5];
            Roots = [chain.AnchorRoot, chain.BlockRoot, root2, root3, root4, root5];
            for (int i = 0; i < blocks.Length; i++)
            {
                _blocksByRoot[Roots[i]] = new ForkedSignedBeaconBlock.OfFulu(blocks[i]);
            }

            _columnsByRoot[chain.BlockRoot] = chain.Columns;
            _columnsByRoot[root3] = columns3;
            SetHead(HeadSlot);
        }

        public BeaconChainStore Store { get; }

        public SlotClock Clock { get; }

        public BeaconChainStatusHolder Status { get; }

        public ILogManager LogManager { get; }

        public DataColumnSidecarPool Pool { get; private set; } = null!;

        public ColumnBackfill Backfill { get; private set; } = null!;

        public Hash256[] Roots { get; }

        public Hash256[] BlobRoots => [Roots[1], Roots[3]];

        public ulong[] Sampled { get; }

        public ConcurrentDictionary<Hash256, byte> WithheldRoots { get; } = [];

        public List<Hash256> RequestedRoots { get; } = [];

        public Action? OnColumnsRequested { get; set; }

        public IReadOnlyList<StubPeer> Peers => _peers;

        public static Fixture Create(ILogManager? logManager = null, ColumnStoreWriter? storeWriter = null)
        {
            ImportableBlobBlock chain = ImportableBlobBlock.Create();
            BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), chain.Spec);
            Fixture fixture = new(chain, store, StoppedAtSlot(chain.Spec, HeadSlot + 1), logManager);

            store.SetAnchor(fixture.Roots[4], AnchorSlot);
            foreach (int slot in new[] { 4, 5 })
            {
                store.PutForkedBlock(fixture.Roots[slot], fixture._blocksByRoot[fixture.Roots[slot]]);
            }

            store.ApplyCanonicalIndexChanges([(AnchorSlot, fixture.Roots[4]), (HeadSlot, fixture.Roots[5])], HeadSlot);
            fixture.UseFreshPool(storeWriter);
            return fixture;
        }

        public Fixture Restart()
        {
            Fixture restarted = new(_chain, Store, _time, _givenLogManager);
            restarted.UseFreshPool();
            _restarts.Add(restarted);
            return restarted;
        }

        private static ManualTimestamper StoppedAtSlot(BeaconChainSpec spec, ulong slot) =>
            new(DateTimeOffset.FromUnixTimeSeconds((long)(spec.GenesisTime + slot * spec.SecondsPerSlot)).UtcDateTime);

        private void UseFreshPool(ColumnStoreWriter? storeWriter = null)
        {
            Pool = new DataColumnSidecarPool(store: Store, clock: Clock, storeWriter: storeWriter);
            _floorCheck = Pool.SeedCompletelyServableFloor(Math.Max(AnchorSlot, Store.GetCanonicalIndexTopSlot() ?? 0));
        }

        public void SetHead(ulong slot) => Status.CurrentStatus = new StatusMessageV2 { ForkDigest = new byte[4], FinalizedRoot = Roots[0], HeadRoot = Roots[(int)Math.Min(slot, HeadSlot)], HeadSlot = slot };

        public void AdvanceSlots(ulong slots)
        {
            _time.Add(TimeSpan.FromSeconds(slots * _chain.Spec.SecondsPerSlot));
            SetHead(Clock.CurrentSlot - 1);
        }

        public StubPeer Peer(string id)
        {
            StubPeer peer = new(
                id,
                HeadSlot,
                (start, count) => [.. Roots.Where(root => _blocksByRoot[root].Slot >= start && _blocksByRoot[root].Slot < start + count).Select(root => _blocksByRoot[root])],
                rootHandler: identifiers =>
                {
                    OnColumnsRequested?.Invoke();
                    lock (RequestedRoots)
                    {
                        RequestedRoots.AddRange(identifiers.Select(static i => i.BlockRoot!));
                    }

                    return [.. identifiers.Where(i => !WithheldRoots.ContainsKey(i.BlockRoot!)).SelectMany(i => i.Columns!.Select(column => _columnsByRoot[i.BlockRoot!][column]))];
                },
                blockRootHandler: roots => [.. roots.Where(_blocksByRoot.ContainsKey).Select(root => _blocksByRoot[root])]);
            _peers.Add(peer);
            return peer;
        }

        public Task StartBackfill(CancellationToken token, params StubPeer[] peers) => StartBackfill(TimeSpan.FromMilliseconds(50), token, peers);

        public Task StartBackfill(TimeSpan retryDelay, CancellationToken token, params StubPeer[] peers)
        {
            StubPool pool = new(peers);
            RangeSync rangeSync = new(pool, LogManager, Pool, _chain.Spec, Clock, _discovery);
            Backfill = new ColumnBackfill(Store, Pool, rangeSync, pool, Clock, _chain.Spec, Status, new DiscoveryNodeCustodySource(_discovery), LogManager)
            {
                RetryDelay = retryDelay,
                WindowPause = TimeSpan.FromMilliseconds(1),
                HeadPollDelay = TimeSpan.FromMilliseconds(10),
            };
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            return _run = Backfill.RunAsync(_cts.Token);
        }

        public async Task UntilFloorAsync(ulong floor, CancellationToken token)
        {
            await _floorCheck.WaitAsync(token);
            while (Pool.EarliestCompletelyServableSlot != floor)
            {
                if (_run.IsCompleted) Assert.Fail($"Backfill ended before reaching floor {floor}");
                await Task.Delay(10, token);
            }
        }

        public async Task StopAsync(Task run)
        {
            await _cts!.CancelAsync();
            await run;
        }

        public async ValueTask DisposeAsync()
        {
            if (_cts is not null)
            {
                await _cts.CancelAsync();
                await _run.WaitAsync(TimeSpan.FromSeconds(10));
                _cts.Dispose();
            }

            foreach (Fixture restarted in _restarts)
            {
                await restarted.DisposeAsync();
            }

            await _discovery.DisposeAsync();
        }
    }
}
