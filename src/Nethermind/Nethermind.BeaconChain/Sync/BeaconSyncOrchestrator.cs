// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.BeaconChain.Sync;

/// <summary>
/// Drives the embedded beacon chain to follow mainnet: kicks the execution layer toward the
/// checkpoint anchor, replays stored canonical blocks, range-syncs to the wall clock, then follows
/// gossip - funneling all consensus work through a single import worker.
/// </summary>
/// <remarks>
/// Threading model: producers (gossip events on libp2p threads, the slot timer, the range-sync
/// feed) only write to a bounded channel; one worker loop consumes it and is the only thread that
/// touches the <see cref="IBlockImporter"/> (and through it the state transition and fork choice,
/// neither of which is thread-safe). A fork-choice head step - <c>engine_forkchoiceUpdated</c>,
/// finality handling, status refresh - runs on the worker after every drained import batch (or
/// every <see cref="HeadStepImportInterval"/> imports while saturated) and on every slot tick.
/// </remarks>
public sealed class BeaconSyncOrchestrator(
    IBeaconChainConfig config,
    BeaconChainSpec spec,
    BeaconChainStore store,
    IBlockImporterFactory importerFactory,
    IEngineDriver engine,
    IBeaconSyncPeerPool peerPool,
    RangeSync rangeSync,
    SlotClock slotClock,
    GossipRouter gossipRouter,
    BeaconChainStatusHolder statusHolder,
    ILogManager logManager,
    BeaconP2P? p2p = null,
    PeerManager? peerManager = null,
    BeaconDiscovery? discovery = null,
    ColumnGossipRouter? columnRouter = null,
    DataColumnSidecarPool? columnPool = null,
    ExecutionPayloadEnvelopePool? envelopePool = null)
{
    /// <summary>Maximum parent-chain depth fetched by root for a gossip block with an unknown parent.</summary>
    private const int MaxBackfillDepth = 32;

    /// <summary>Peers tried per by-root fetch before giving the block up.</summary>
    private const int MaxBackfillPeersPerRequest = 3;

    /// <summary>By-root backfills started per wall-clock slot for gossip blocks with an unknown parent.</summary>
    internal const int MaxBackfillsPerSlot = 4;

    /// <summary>Gossip blocks the backfill budget refused that are held for their parent at once.</summary>
    internal const int MaxHeldRefusedBackfills = 4 * MaxBackfillsPerSlot;

    private const int MaxPendingGossipBlocks = 128;

    /// <summary>Cap on blocks awaiting a data/engine-availability retry, so a stuck peer or a stalled EL cannot grow this without bound.</summary>
    private const int MaxPendingRetryBlocks = 128;

    /// <summary>Epochs a block or envelope may wait in a retry set, so under stalled finality a stuck one does not keep its place.</summary>
    private const ulong MaxPendingRetryAgeEpochs = 2;

    /// <summary>Envelopes held per block root, first come, so forgeries naming one block cannot take every place.</summary>
    private const int MaxPendingEnvelopesPerBlock = 4;

    /// <summary>Cap on envelopes held for a block not imported yet, and separately on envelopes awaiting a data or engine retry.</summary>
    private const int MaxPendingEnvelopes = 128;

    /// <summary>Cap on imported Gloas blocks whose sampled columns are still recovered by root on each slot tick.</summary>
    private const int MaxColumnRecoveryBlocks = 32;

    private const int RecentEnvelopeRequestCapacity = 1024;

    internal const int WorkQueueCapacity = 512;

    internal const int VoteQueueCapacity = 1024;

    /// <summary>The most gossip votes and slashings verified per worker pass, so a flood of them delays queued blocks by one batch at most.</summary>
    internal const int VotesPerPass = 64;

    /// <summary>Head-step cadence while the work queue never drains (deep range sync).</summary>
    private const int HeadStepImportInterval = 64;

    /// <summary>Head distance (~2 epochs) below which gossip is started while range sync finishes the residual gap.</summary>
    private const ulong GossipStartDistanceSlots = 64;

    // At a slot tick the newest block is usually the previous slot's, and one missed proposal adds another.
    private const ulong FollowingHeadSlackSlots = 2;

    private const long ProgressLogIntervalMs = 1000;

    // Most mainnet dials fail (peers at capacity); high parallelism shortens time-to-first-peer.
    private const int ConcurrentDials = 16;

    private readonly ILogger _logger = logManager.GetClassLogger<BeaconSyncOrchestrator>();
    private readonly Channel<WorkItem> _work = Channel.CreateBounded<WorkItem>(
        new BoundedChannelOptions(WorkQueueCapacity) { SingleReader = true });

    // Each queued vote costs a BLS verify and a forged one is never penalized, so votes wait apart: a flood of them must not fill _work and drop gossip blocks.
    private readonly Channel<QueuedVote> _votes = Channel.CreateBounded<QueuedVote>(
        new BoundedChannelOptions(VoteQueueCapacity) { SingleReader = true });

    /// <summary>1 while a <see cref="VoteWakeItem"/> may be queued in the work channel, so a vote flood adds at most one item there.</summary>
    private int _voteWakeQueued;

    /// <summary>The newest slot tick the worker has read; a vote queued after a newer tick waits for that tick.</summary>
    private ulong _reachedSlotTick;

    /// <summary>The newest slot tick queued, written before the tick is; a queued tick older than it is skipped.</summary>
    private ulong _newestSlotTick;

    private ulong _statusLogEpoch;

    /// <summary>Gossip blocks waiting for their parent, keyed by the unknown parent root.</summary>
    private readonly Dictionary<Hash256, List<ForkedSignedBeaconBlock>> _pendingByParent = [];

    /// <summary>Blocks that returned <see cref="BlockImportResult.DataUnavailable"/>, <see cref="BlockImportResult.EngineUnavailable"/> or <see cref="BlockImportResult.ParentPayloadUnverified"/>, keyed by block root, awaiting a retry.</summary>
    private readonly Dictionary<Hash256, PendingRetry> _pendingRetry = [];

    /// <summary>The custodians asked for the missing columns of each block in <see cref="_pendingRetry"/>, kept across slots so every custodian is reached.</summary>
    private readonly Dictionary<Hash256, RangeSync.ColumnFetchRotation> _columnFetchRotations = [];

    /// <summary>The roots whose by-root column fetch is running, at most one each; the fetch runs off the worker and ends with a <see cref="ColumnFetchEndedItem"/>.</summary>
    private readonly HashSet<Hash256> _columnFetchesInFlight = [];

    /// <summary>The block whose refused-block fetch is running, so the blocks a full retry set refuses cost at most one fetch at a time.</summary>
    private Hash256? _refusedFetchRoot;

    /// <summary>The roots the sidecar pool may wake the worker for, once every sampled column of one is held.</summary>
    private readonly HashSet<Hash256> _columnWatched = [];

    /// <summary>The roots of the blocks in <see cref="_pendingByParent"/> held for a parent waiting on a payload.</summary>
    private readonly HashSet<Hash256> _heldForPayload = [];

    /// <summary>Envelopes that answered <see cref="ExecutionPayloadEnvelopeImportResult.UnknownBlock"/>, held until the block they name imports.</summary>
    /// <remarks>specs/gloas/p2p-interface.md <c>execution_payload</c>: an envelope for a block not yet seen MAY be queued until the block is retrieved.</remarks>
    private readonly EnvelopeParking _pendingEnvelopesByBlock = new(MaxPendingEnvelopesPerBlock, MaxPendingEnvelopes);

    /// <summary>Envelopes that answered <see cref="ExecutionPayloadEnvelopeImportResult.DataUnavailable"/> or <see cref="ExecutionPayloadEnvelopeImportResult.EngineUnavailable"/>, retried on each slot tick.</summary>
    private readonly EnvelopeParking _pendingEnvelopeRetry = new(MaxPendingEnvelopesPerBlock, MaxPendingEnvelopes);

    /// <summary>The slot each parent envelope was last requested by root, so a parked child asks at most once per slot.</summary>
    private readonly LruCache<Hash256, ulong> _envelopeRequestedAtSlot = new(RecentEnvelopeRequestCapacity, "beacon sync envelope by-root requests");

    /// <summary>Imported Gloas blocks whose bid commits blobs, by root, whose sampled columns are recovered by root until all are held.</summary>
    private readonly Dictionary<Hash256, ColumnRecovery> _columnRecovery = [];

    /// <summary>The unknown parent roots whose backfill started in wall-clock slot <see cref="_backfillSlot"/>.</summary>
    private readonly HashSet<Hash256> _backfilledParents = [];
    private int _backfillsThisSlot;
    private ulong _backfillSlot;

    /// <summary>The gossip blocks held in <see cref="_pendingByParent"/> after the backfill budget refused them, oldest first.</summary>
    private readonly Queue<ForkedSignedBeaconBlock> _heldForBackfill = new();

    private readonly ConcurrentDictionary<string, byte> _dialedPeerIds = new();

    private IBlockImporter? _importer;
    private volatile Tip _syncTip = new(Hash256.Zero, 0);

    /// <summary>Cancelled and replaced to restart range sync; see <see cref="ResumeRangeSyncFromHead"/>.</summary>
    private CancellationTokenSource _rangeSyncRestart = new();
    private ulong? _rangeSyncResumedAtSlot;

    /// <summary>The head slot at the latest slot tick, and the first tick it was seen at.</summary>
    private (ulong HeadSlot, ulong SinceSlot)? _headSlotSeen;
    private Hash256 _anchorExecutionHash = Hash256.Zero;
    private ulong _anchorSlot;
    private HeadView? _lastHead;
    private bool _elInSync;

    // The last forkchoiceUpdated sent, so an unchanged one is not repeated each slot.
    private (ForkchoiceHashes Hashes, long SentAtMs, PayloadStatusV1 Status)? _lastForkchoice;

    /// <summary>How often an unchanged <c>forkchoiceUpdated</c> is still sent; well inside the EL's default 300 s CL liveness window.</summary>
    internal static readonly TimeSpan ForkchoiceResendInterval = TimeSpan.FromSeconds(60);
    private bool _importedSinceHeadStep;
    private int _importsSinceHeadStep;
    private int _pendingCount;
    private byte[] _currentDigest = [];
    private (ulong Epoch, byte[] Digest)? _nextRotation;
    private (byte[] Digest, bool Gloas)[] _gossipDigests = [];
    private ulong _reconciledEpoch;
    private bool _columnGossipStarted;
    private Func<string, ITopic>? _getTopic;
    private readonly DiscoveryNodeCustodySource _custody = new(discovery);
    private ulong _progressLogSlot;
    private long _progressLogMs;
    private long _blocksSinceProgressLog;
    private long _importMsSinceProgressLog;
    private ulong _newPayloadMsAtProgressLog;

    private sealed record Tip(Hash256 Root, ulong Slot);

    internal abstract record WorkItem;
    internal sealed record RangeBlockItem(ForkedSignedBeaconBlock Block) : WorkItem;
    internal sealed record GossipBlockItem(ForkedSignedBeaconBlock Block) : WorkItem;
    internal sealed record GossipAggregateItem(SignedAggregateAndProof Aggregate) : WorkItem;
    internal sealed record GossipGloasAggregateItem(SignedAggregateAndProofGloas Aggregate) : WorkItem;
    internal sealed record GossipAttesterSlashingItem(AttesterSlashing Slashing) : WorkItem;
    internal sealed record GossipGloasAttesterSlashingItem(AttesterSlashingGloas Slashing) : WorkItem;
    internal sealed record GossipPayloadAttestationItem(PayloadAttestationMessage Message) : WorkItem;
    internal sealed record SlotTickItem(ulong Slot) : WorkItem;

    /// <summary>The sidecar pool holds every sampled column awaited for <paramref name="BlockRoot"/>; queued at most once per watch.</summary>
    internal sealed record ColumnsHeldItem(Hash256 BlockRoot) : WorkItem;

    /// <summary>A by-root column fetch for <paramref name="BlockRoot"/> ended; <paramref name="Complete"/> is whether every sampled column is then held, and <paramref name="Refused"/> the deferred block the full retry set could not hold.</summary>
    internal sealed record ColumnFetchEndedItem(Hash256 BlockRoot, bool Complete, ForkedSignedBeaconBlock? Refused = null) : WorkItem;

    /// <summary>Wakes the worker for queued gossip votes; carries no work of its own.</summary>
    private sealed record VoteWakeItem : WorkItem
    {
        public static readonly VoteWakeItem Instance = new();
    }

    /// <summary>A queued gossip vote; <paramref name="NewestSlotTick"/> is the newest slot tick queued before it.</summary>
    private readonly record struct QueuedVote(WorkItem Item, ulong NewestSlotTick);

    /// <summary>An execution payload envelope to import; <paramref name="Source"/> is the req/resp peer that served it, if any.</summary>
    internal abstract record EnvelopeItem(SignedExecutionPayloadEnvelope Envelope, IBeaconSyncPeer? Source) : WorkItem;
    internal sealed record GossipEnvelopeItem(SignedExecutionPayloadEnvelope Envelope) : EnvelopeItem(Envelope, null);
    internal sealed record RangeEnvelopeItem(SignedExecutionPayloadEnvelope Envelope, IBeaconSyncPeer Source) : EnvelopeItem(Envelope, Source);
    internal sealed record FetchedEnvelopeItem(SignedExecutionPayloadEnvelope Envelope, IBeaconSyncPeer? Source) : EnvelopeItem(Envelope, Source);

    private readonly record struct PendingRetry(ForkedSignedBeaconBlock Block, ulong QueuedAtSlot);

    private sealed record ColumnRecovery(ExecutionPayloadBid Bid, ulong QueuedAtSlot, ulong LastAttemptSlot, RangeSync.ColumnFetchRotation Rotation);

    /// <summary>Whether gossip has started; settable by tests to keep it from starting.</summary>
    internal bool GossipStarted { get; set; }

    internal byte[] CurrentGossipDigest => _currentDigest;

    internal (Hash256 Root, ulong Slot) SyncTip => (_syncTip.Root, _syncTip.Slot);

    internal ChannelWriter<WorkItem> WorkWriter => _work.Writer;

    /// <summary>Completes when a work item is queued, without taking it; for tests that drive the worker by hand.</summary>
    internal ValueTask<bool> WaitForWorkAsync(CancellationToken token) => _work.Reader.WaitToReadAsync(token);

    /// <summary>The work items queued and not yet taken; for tests.</summary>
    internal int QueuedWorkCount => _work.Reader.Count;

    /// <summary>The by-root column fetches running off the worker; for tests.</summary>
    internal int ColumnFetchesInFlight => _columnFetchesInFlight.Count;

    /// <summary>The gossip blocks held for a parent, bounded by <see cref="MaxPendingGossipBlocks"/>; for tests.</summary>
    internal int PendingGossipBlockCount => _pendingCount;

    /// <summary>The blocks whose by-root column fetches are tracked, bounded by the retry set; for tests.</summary>
    internal int ColumnFetchRotationCount => _columnFetchRotations.Count;

    /// <summary>The blocks awaiting a data or engine retry, bounded by <see cref="MaxPendingRetryBlocks"/>; for tests.</summary>
    internal int PendingRetryBlockCount => _pendingRetry.Count;

    /// <summary>Runs the full sync flow from the given anchor until cancelled.</summary>
    /// <param name="afterEngineKick">Runs once the execution layer has been pointed at the anchor and before any block is imported, so slow start-up work does not delay that first call.</param>
    public async Task RunAsync(ForkedBeaconState anchorState, ForkedSignedBeaconBlock anchorBlock, Hash256 anchorRoot, CancellationToken token, Action? afterEngineKick = null)
    {
        if (p2p is null || peerManager is null || discovery is null)
        {
            throw new InvalidOperationException($"{nameof(BeaconSyncOrchestrator)} requires the P2P components to run");
        }

        // A run stopped before the kick must neither build the importer nor reach the execution layer.
        token.ThrowIfCancellationRequested();

        Initialize(importerFactory.Create(anchorState, anchorBlock, anchorRoot), anchorBlock, anchorRoot);

        // Engine kick: point the execution layer at the anchor payload so it starts beacon/snap
        // syncing toward it; SYNCING is the expected (successful) answer here.
        if (_logger.IsInfo) _logger.Info($"Beacon sync starting from anchor slot {_anchorSlot} ({anchorRoot}); kicking execution layer with forkchoiceUpdated(head=safe=finalized={_anchorExecutionHash})");
        PayloadStatusV1 kick = await KickExecutionAsync(anchorRoot).WaitAsync(token);
        if (_logger.IsInfo) _logger.Info($"Engine kick returned {kick.Status}{(kick.Status == PayloadStatus.Syncing ? " - execution layer is syncing toward the anchor" : "")}");
        afterEngineKick?.Invoke();

        await ReplayStoredBlocksAsync(token);

        await p2p.StartAsync(token);
        await discovery.Start(token);

        // The loops only stop on cancellation, so a fault in any of them stops all the others
        // before it is propagated to the caller.
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task[] tasks =
        [
            RunWorkerAsync(linked.Token),
            PumpSlotTicksAsync(linked.Token),
            RunRangeSyncFeedAsync(linked.Token),
            peerManager.Run(linked.Token),
            RunDiscoveryDialLoopAsync(linked.Token),
        ];
        Task first = await Task.WhenAny(tasks);
        await linked.CancelAsync();
        foreach (Task task in tasks)
        {
            if (ReferenceEquals(task, first))
            {
                continue;
            }

            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("Beacon sync loop failed while stopping.", e);
            }
        }

        await first;
    }

    /// <summary>Binds the importer and anchor-derived bookkeeping; the synchronous head of <see cref="RunAsync"/>.</summary>
    /// <remarks>
    /// A Gloas anchor's execution hash is its bid's <c>parent_block_hash</c> (specs/gloas/fork-choice.md
    /// <c>notify_forkchoice_updated</c>), which <c>process_execution_payload_bid</c> asserts equals the
    /// anchor state's <c>latest_block_hash</c>; the bid's own payload may never have been revealed.
    /// </remarks>
    internal void Initialize(IBlockImporter importer, ForkedSignedBeaconBlock anchorBlock, Hash256 anchorRoot)
    {
        _importer = importer;
        _anchorSlot = anchorBlock.Slot;
        _anchorExecutionHash = anchorBlock switch
        {
            ForkedSignedBeaconBlock.OfFulu fulu => fulu.Block.Message!.Body!.ExecutionPayload!.BlockHash!,
            ForkedSignedBeaconBlock.OfGloas gloas => gloas.Block.Message!.Body!.SignedExecutionPayloadBid!.Message!.ParentBlockHash!,
            _ => throw new NotSupportedException($"Unhandled anchor block {anchorBlock.GetType().Name}"),
        };
        _syncTip = new Tip(anchorRoot, _anchorSlot);
        _progressLogSlot = _anchorSlot;
        _progressLogMs = slotClock.UnixMilliseconds;
        _newPayloadMsAtProgressLog = Metrics.BeaconChainNewPayloadMilliseconds;

        ulong epoch = slotClock.CurrentEpoch;
        _currentDigest = GossipTopics.CurrentDigest(spec, epoch);
        _nextRotation = GossipTopics.NextRotation(spec, epoch);

        importer.OnSlotTick(slotClock.CurrentSlot);
        statusHolder.CurrentStatus = new StatusMessageV2
        {
            ForkDigest = _currentDigest,
            FinalizedRoot = anchorRoot,
            FinalizedEpoch = spec.GetEpoch(_anchorSlot),
            HeadRoot = anchorRoot,
            HeadSlot = _anchorSlot,
            EarliestAvailableSlot = EarliestAvailableSlot(),
        };
    }

    /// <summary>The <c>earliest_available_slot</c> to advertise in Status v2.</summary>
    /// <remarks>
    /// fulu/p2p-interface.md Status v2: it is the slot of the earliest available block, the anchor, except that a node able to
    /// serve every block of the sidecar retention period but not every sidecar advertises the earliest slot from which it can
    /// serve all sidecars. So the held columns raise it only once the anchor is at or below the start of
    /// <c>data_column_serve_range</c>, and only when they start inside that range. The range ends at the current slot, so the
    /// slot after it is servable even with no columns held.
    /// </remarks>
    private ulong EarliestAvailableSlot()
    {
        if (columnPool is null || slotClock.CurrentEpoch < spec.FuluForkEpoch)
        {
            return _anchorSlot;
        }

        ulong serveFrom = DataAvailabilityBoundary.ComputeStartSlot(slotClock.CurrentEpoch, spec);
        if (_anchorSlot > serveFrom)
        {
            return _anchorSlot;
        }

        ulong columnsFrom = Math.Min(columnPool.EarliestCompletelyServableSlot, slotClock.CurrentSlot + 1);
        return columnsFrom <= serveFrom ? _anchorSlot : columnsFrom;
    }

    /// <summary>
    /// Re-imports the canonical blocks already persisted between the anchor and the wall clock, so
    /// a restart does not refetch them from the network.
    /// </summary>
    /// <remarks>
    /// Blocks are imported with signature verification off: they were fully verified before being
    /// persisted. Parent linkage is still checked so a stale index tail (e.g. entries past an
    /// unfinalized reorg point) stops the replay and leaves the rest to range sync. Envelopes are not
    /// persisted, so a stored Gloas block that builds on its parent's full payload stands in for that
    /// parent's verified envelope (see <see cref="IBlockImporter.Import"/>).
    /// </remarks>
    internal async Task ReplayStoredBlocksAsync(CancellationToken token)
    {
        IBlockImporter importer = _importer!;
        Hash256 expectedParent = _syncTip.Root;
        int replayed = 0;
        ulong wallSlot = slotClock.CurrentSlot;
        for (ulong slot = _syncTip.Slot + 1; slot <= wallSlot; slot++)
        {
            token.ThrowIfCancellationRequested();
            if (!store.TryGetCanonicalRoot(slot, out Hash256? root) || !store.TryGetForkedBlock(root, out ForkedSignedBeaconBlock? block))
            {
                continue;
            }

            if (block.ParentRoot != expectedParent || importer.Import(block, root, verifySignatures: false) != BlockImportResult.Imported)
            {
                break;
            }

            expectedParent = root;
            _syncTip = new Tip(root, slot);
            replayed++;
        }

        if (replayed > 0)
        {
            if (_logger.IsInfo) _logger.Info($"Replayed {replayed} canonical beacon blocks from the store up to slot {_syncTip.Slot}");
            await RunHeadStepAsync(token);
        }
    }

    /// <summary>The single consumer of the work and vote channels; completes when the work channel is completed or the token fires.</summary>
    internal async Task RunWorkerAsync(CancellationToken token)
    {
        ChannelReader<WorkItem> reader = _work.Reader;
        while (await reader.WaitToReadAsync(token))
        {
            await ProcessQueuedAsync(token);
        }
    }

    /// <summary>
    /// Processes at most <see cref="VotesPerPass"/> queued gossip votes, then every queued work item, each slot tick after every vote
    /// queued before it, then runs a head step if any imported; one pass of <see cref="RunWorkerAsync"/>.
    /// </summary>
    internal async Task ProcessQueuedAsync(CancellationToken token)
    {
        // Votes first, so one queued before a slot tick is not checked against the next slot, but none queued after a tick the worker has not reached (gloas/fork-choice.md on_payload_attestation_message).
        for (int i = 0; i < VotesPerPass && TryReadVoteBefore(_reachedSlotTick + 1, out QueuedVote vote); i++)
        {
            await ProcessItemAsync(vote.Item, token);
        }

        while (_work.Reader.TryRead(out WorkItem? item))
        {
            if (item is SlotTickItem tick)
            {
                while (TryReadVoteBefore(tick.Slot, out QueuedVote vote))
                {
                    await ProcessItemAsync(vote.Item, token);
                }

                _reachedSlotTick = Math.Max(_reachedSlotTick, tick.Slot);
            }

            await ProcessItemAsync(item, token);
        }

        // Cleared after the drain, which may have read the queued wake, and fenced before the peek, so leftover or later votes always wake the worker;
        // a vote waiting for a tick not yet reached is woken by that tick.
        Interlocked.Exchange(ref _voteWakeQueued, 0);
        if (_votes.Reader.TryPeek(out QueuedVote next) && next.NewestSlotTick <= _reachedSlotTick)
        {
            WakeForVotes();
        }

        if (_importedSinceHeadStep)
        {
            await RunHeadStepAsync(token);
        }
    }

    // Reads the next vote only if it was queued before the tick of tickSlot; the single reader makes the peek and the read one step.
    private bool TryReadVoteBefore(ulong tickSlot, out QueuedVote vote) =>
        _votes.Reader.TryPeek(out vote) && vote.NewestSlotTick < tickSlot && _votes.Reader.TryRead(out vote);

    private async Task ProcessItemAsync(WorkItem item, CancellationToken token)
    {
        switch (item)
        {
            case RangeBlockItem range:
                await ImportBlockAsync(range.Block, token);
                break;
            case GossipBlockItem gossip:
                await ProcessGossipBlockAsync(gossip.Block, token);
                break;
            case GossipAggregateItem aggregate:
                _importer!.OnGossipAggregate(aggregate.Aggregate);
                break;
            case GossipGloasAggregateItem aggregate:
                _importer!.OnGossipAggregate(aggregate.Aggregate);
                break;
            case GossipAttesterSlashingItem { Slashing: AttesterSlashing slashing }:
                if (_importer!.OnGossipAttesterSlashing(slashing))
                {
                    MarkSlashedIndicesSeen(slashing.Attestation1!.AttestingIndices!, slashing.Attestation2!.AttestingIndices!);
                }

                break;
            case GossipGloasAttesterSlashingItem { Slashing: AttesterSlashingGloas slashing }:
                if (_importer!.OnGossipAttesterSlashing(slashing))
                {
                    MarkSlashedIndicesSeen(slashing.Attestation1!.AttestingIndices!, slashing.Attestation2!.AttestingIndices!);
                }

                break;
            case GossipPayloadAttestationItem payloadAttestation:
                if (!gossipRouter.IsPayloadAttestationVerified(payloadAttestation.Message) && _importer!.OnGossipPayloadAttestation(payloadAttestation.Message))
                {
                    gossipRouter.MarkPayloadAttestationVerified(payloadAttestation.Message);
                }

                break;
            // on_tick steps through every skipped slot, so only the newest tick of a backlog needs the per-slot work;
            // each older one would send another forkchoiceUpdated for the same head.
            case SlotTickItem tick when tick.Slot >= Volatile.Read(ref _newestSlotTick):
                await ProcessSlotAsync(tick.Slot, token);
                break;
            case EnvelopeItem envelope:
                await ImportEnvelopeItemAsync(envelope, token);
                break;
            case ColumnsHeldItem held:
                // The pool dropped the fired watch; one a re-arm set since is dropped too, so none is orphaned.
                if (_columnWatched.Remove(held.BlockRoot))
                {
                    columnPool!.Unwatch(held.BlockRoot);
                }

                await RetryOnColumnsAsync(held.BlockRoot, token);
                break;
            case ColumnFetchEndedItem ended:
                _columnFetchesInFlight.Remove(ended.BlockRoot);
                if (ended.Refused is not null)
                {
                    _refusedFetchRoot = null;
                }

                if (ended.Complete)
                {
                    _columnRecovery.Remove(ended.BlockRoot);
                    await RetryOnColumnsAsync(ended.BlockRoot, token);
                    if (ended.Refused is { } refused && !_pendingRetry.ContainsKey(ended.BlockRoot) && !_importer!.IsKnown(ended.BlockRoot))
                    {
                        await ImportBlockAsync(refused, token, retryingOnColumns: true);
                    }
                }

                break;
        }
    }

    // phase0/p2p-interface.md attester_slashing: the seen set holds the intersecting indices of slashings whose signatures verified.
    private void MarkSlashedIndicesSeen(ulong[] indices1, ulong[] indices2)
    {
        HashSet<ulong> second = [.. indices2];
        List<ulong> intersecting = [];
        foreach (ulong index in indices1)
        {
            if (second.Contains(index))
            {
                intersecting.Add(index);
            }
        }

        gossipRouter.MarkSlashedIndicesSeen(intersecting);
    }

    /// <summary>
    /// Imports one block and, on success, drains any gossip blocks that were waiting for it. The
    /// single choke point for all four callers of <see cref="IBlockImporter.Import"/> that can import, so this is
    /// also where a <see cref="BlockImportResult.DataUnavailable"/>, <see cref="BlockImportResult.ParentPayloadUnverified"/> or
    /// <see cref="BlockImportResult.EngineUnavailable"/> result is remembered for a later retry -
    /// wiring it in at only one call site would leave the other three silently dropping it.
    /// </summary>
    /// <param name="retryingOnColumns">Whether this import was woken by the columns it waited for, so a repeat deferral waits for the slot tick instead of watching and fetching again.</param>
    internal async Task<BlockImportResult> ImportBlockAsync(ForkedSignedBeaconBlock block, CancellationToken token, bool retryingOnColumns = false)
    {
        Hash256 root = block.ComputeMessageRoot();
        long startMs = Environment.TickCount64;
        BlockImportResult result = _importer!.Import(block, root, verifySignatures: true);

        // These results come after the importer verified the proposer signature.
        if (result is BlockImportResult.Imported or BlockImportResult.EngineUnavailable or BlockImportResult.ParentPayloadUnverified)
        {
            gossipRouter.MarkProposalSeen(block.Slot, block.ProposerIndex);
        }

        if (result == BlockImportResult.Imported)
        {
            Metrics.BeaconChainBlocksImported++;
            Metrics.BeaconChainLastBlockImportMs = Environment.TickCount64 - startMs;
            _importMsSinceProgressLog += Metrics.BeaconChainLastBlockImportMs;
            _pendingRetry.Remove(root);
            if (TrackColumnRecovery(root, block) is { } recovery)
            {
                RecoverColumns(root, recovery, slotClock.CurrentSlot, token);
            }

            await OnImportedAsync(root, block.Slot, token);
        }
        else if (result is BlockImportResult.DataUnavailable or BlockImportResult.EngineUnavailable or BlockImportResult.ParentPayloadUnverified)
        {
            if (!QueuePendingRetry(root, block))
            {
                DropPendingChildren(root);
                // The refused block's own fetch feeds back one import attempt; that attempt never fetches again, so the set stays the only place a block waits.
                if (result == BlockImportResult.DataUnavailable && !retryingOnColumns)
                {
                    AwaitColumns(block, root, token, refused: true);
                }
            }
            // A child of a deferred parent waits on that parent's import, not on an envelope fork choice could record.
            else if (result == BlockImportResult.ParentPayloadUnverified
                && _importer!.IsKnown(block.ParentRoot)
                && await RecoverParentEnvelopeAsync(block.ParentRoot, token)
                && _importer!.IsKnown(root))
            {
                // The recovered envelope re-drove this block from the retry set, so the caller must see it imported.
                return BlockImportResult.Imported;
            }
            else if (result == BlockImportResult.DataUnavailable && !retryingOnColumns)
            {
                AwaitColumns(block, root, token);
            }
        }
        else
        {
            // A retried block answers UnknownParent once its parent's state is gone, so it never imports.
            bool wasRetried = _pendingRetry.Remove(root);
            if (result == BlockImportResult.Invalid || (wasRetried && result == BlockImportResult.UnknownParent))
            {
                DropPendingChildren(root);
            }
        }

        // Kept only while the block waits for a retry, which bounds the rotations to MaxPendingRetryBlocks.
        if (!_pendingRetry.ContainsKey(root))
        {
            _columnFetchRotations.Remove(root);
            ReleaseColumnWatch(root);
        }

        return result;
    }

    /// <summary>
    /// Has the pool wake the worker once a deferred Fulu block's sampled columns are all held, and starts fetching its missing
    /// ones by root from a bounded number of custodians, rotating through every connected custodian of a missing column across calls and slots.
    /// </summary>
    /// <remarks>
    /// A custodian behind peers that answered with nothing, or one that connected since the last attempt, is still reached
    /// (fulu/das-core.md, every sampled column), while one fetch costs a bounded number of requests however many peers are connected.
    /// </remarks>
    /// <param name="refused">Whether the retry set refused the block: it is neither watched nor given a stored rotation, and the fetch ends with an import attempt of it.</param>
    private void AwaitColumns(ForkedSignedBeaconBlock block, Hash256 root, CancellationToken token, bool refused = false)
    {
        if (block is not ForkedSignedBeaconBlock.OfFulu { Block.Message: { Body.BlobKzgCommitments.Length: > 0 } message })
        {
            return;
        }

        RangeSync.ColumnFetchRotation? rotation;
        if (refused)
        {
            if (_refusedFetchRoot is not null)
            {
                return;
            }

            rotation = new RangeSync.ColumnFetchRotation(slotClock);
        }
        else
        {
            WatchColumns(root, gloas: false);
            if (!_columnFetchRotations.TryGetValue(root, out rotation))
            {
                _columnFetchRotations[root] = rotation = new RangeSync.ColumnFetchRotation(slotClock);
            }
        }

        if (StartColumnFetch(root, fetchToken => rangeSync.FetchColumnsByRootAsync(root, message, rotation, fetchToken), token, refused ? block : null) && refused)
        {
            _refusedFetchRoot = root;
        }
    }

    /// <summary>Asks the pool to queue one <see cref="ColumnsHeldItem"/> for <paramref name="root"/> when every sampled column of it is held.</summary>
    private void WatchColumns(Hash256 root, bool gloas)
    {
        if (columnPool is not null
            && _custody.Current is { } custody
            && columnPool.TryWatch(root, custody.SampledColumns, gloas, () => _work.Writer.TryWrite(new ColumnsHeldItem(root))))
        {
            _columnWatched.Add(root);
        }
    }

    /// <summary>Drops the pool's watch on <paramref name="root"/> once no block or envelope waits on it, so a sidecar for it queues nothing.</summary>
    private void ReleaseColumnWatch(Hash256 root)
    {
        if (_columnWatched.Count == 0 || _pendingRetry.ContainsKey(root) || _pendingEnvelopeRetry.Contains(root) || !_columnWatched.Remove(root))
        {
            return;
        }

        columnPool!.Unwatch(root);
    }

    /// <summary>Drops the watches of roots whose block or envelope left the retry sets by a path other than a block import.</summary>
    private void ReleaseIdleColumnWatches()
    {
        if (_columnWatched.Count == 0)
        {
            return;
        }

        Hash256[] watched = [.. _columnWatched];
        foreach (Hash256 root in watched)
        {
            ReleaseColumnWatch(root);
        }
    }

    /// <summary>Runs <paramref name="fetch"/> off the worker unless one is running for <paramref name="root"/>; it ends with a <see cref="ColumnFetchEndedItem"/>.</summary>
    /// <returns>Whether a fetch was started.</returns>
    private bool StartColumnFetch(Hash256 root, Func<CancellationToken, Task<bool>> fetch, CancellationToken token, ForkedSignedBeaconBlock? refused = null)
    {
        if (!_columnFetchesInFlight.Add(root))
        {
            return false;
        }

        _ = RunColumnFetchAsync(root, fetch, token, refused);
        return true;
    }

    /// <remarks>Touches no worker state: the fetched sidecars reach the pool, which wakes the worker, and the end is reported as a work item.</remarks>
    private async Task RunColumnFetchAsync(Hash256 root, Func<CancellationToken, Task<bool>> fetch, CancellationToken token, ForkedSignedBeaconBlock? refused)
    {
        bool complete = false;
        if (_logger.IsDebug) _logger.Debug($"By-root column fetch for {root} started");
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            complete = await fetch(token);
            if (_logger.IsDebug) _logger.Debug($"By-root column fetch for {root} ended after {Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F0} ms, {(complete ? "every sampled column held" : "columns still missing")}");
        }
        catch (Exception e)
        {
            if (!token.IsCancellationRequested && _logger.IsDebug) _logger.Debug($"By-root column fetch for {root} failed after {Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F0} ms and is retried on the slot tick: {e.Message}");
        }

        try
        {
            await _work.Writer.WriteAsync(new ColumnFetchEndedItem(root, complete, refused), token);
        }
        catch (Exception e) when (e is OperationCanceledException or ChannelClosedException)
        {
        }
    }

    /// <summary>Retries the block and the envelopes held for <paramref name="root"/> now that their columns are held, without waiting for the slot tick.</summary>
    private async Task RetryOnColumnsAsync(Hash256 root, CancellationToken token)
    {
        if (_pendingRetry.TryGetValue(root, out PendingRetry pending))
        {
            await ImportBlockAsync(pending.Block, token, retryingOnColumns: true);
        }

        foreach (ParkedEnvelope parked in _pendingEnvelopeRetry.HeldFor(root))
        {
            await RetryParkedEnvelopeAsync(parked, token, retryingOnColumns: true);
        }
    }

    /// <summary>Remembers a block for <see cref="DrainPendingRetriesAsync"/>; silently drops it once <see cref="MaxPendingRetryBlocks"/> is reached, same as <see cref="QueuePendingGossipBlock"/> does for its list.</summary>
    /// <returns>Whether the block is held for a retry.</returns>
    private bool QueuePendingRetry(Hash256 root, ForkedSignedBeaconBlock block)
    {
        if (_pendingRetry.ContainsKey(root))
        {
            return true;
        }

        if (_pendingRetry.Count >= MaxPendingRetryBlocks)
        {
            return false;
        }

        _pendingRetry[root] = new PendingRetry(block, slotClock.CurrentSlot);
        return true;
    }

    /// <summary>Forgets the gossip blocks held for <paramref name="parentRoot"/>, and theirs in turn, once that parent can no longer import.</summary>
    /// <remarks>Otherwise they keep their share of <see cref="MaxPendingGossipBlocks"/> for the process lifetime.</remarks>
    private void DropPendingChildren(Hash256 parentRoot)
    {
        if (!_pendingByParent.ContainsKey(parentRoot))
        {
            return;
        }

        Stack<Hash256> dropped = new();
        dropped.Push(parentRoot);
        while (dropped.TryPop(out Hash256? root))
        {
            if (!_pendingByParent.Remove(root, out List<ForkedSignedBeaconBlock>? children))
            {
                continue;
            }

            _pendingCount -= children.Count;
            foreach (ForkedSignedBeaconBlock child in children)
            {
                Hash256 childRoot = child.ComputeMessageRoot();
                _heldForPayload.Remove(childRoot);
                dropped.Push(childRoot);
            }
        }
    }

    /// <summary>
    /// Retries every pending block once per slot tick, straight through <see cref="ImportBlockAsync"/>
    /// - never through <see cref="ProcessGossipBlockAsync"/>, whose seen-proposal gate would drop the
    /// retry as a repeat. A pending block whose slot has fallen behind the finalized checkpoint is
    /// dropped instead of retried, and so is one that has waited more than <see cref="MaxPendingRetryAgeEpochs"/>
    /// since it was queued, which bounds a stuck block under stalled finality; with <see cref="MaxPendingRetryBlocks"/>,
    /// that is what stops a stuck block from being retried forever.
    /// </summary>
    private async Task DrainPendingRetriesAsync(CancellationToken token)
    {
        if (_pendingRetry.Count == 0)
        {
            return;
        }

        ulong finalizedSlot = FinalizedSlot;
        ulong currentSlot = slotClock.CurrentSlot;
        List<KeyValuePair<Hash256, PendingRetry>> retries = [.. _pendingRetry];
        foreach ((Hash256 root, PendingRetry retry) in retries)
        {
            if (retry.Block.Slot <= finalizedSlot || IsRetryExpired(retry.QueuedAtSlot, currentSlot))
            {
                _pendingRetry.Remove(root);
                _columnFetchRotations.Remove(root);
                ReleaseColumnWatch(root);
                DropPendingChildren(root);
                if (retry.Block.Slot > finalizedSlot && _logger.IsWarn)
                    _logger.Warn($"Dropping block {root} at slot {retry.Block.Slot}: its data did not become available within {MaxPendingRetryAgeEpochs} epochs");
                // Nothing else brings back a dropped block the head waits on: range sync re-delivers it from the head.
                if (retry.Block.Slot > finalizedSlot && retry.Block.ParentRoot == _lastHead?.HeadRoot)
                {
                    ResumeRangeSyncFromHead(currentSlot);
                }

                continue;
            }

            await ImportBlockAsync(retry.Block, token);
        }
    }

    private ulong FinalizedSlot => _lastHead is { } head ? BeaconStateAccessors.ComputeStartSlotAtEpoch(head.Finalized.Epoch) : 0;

    private bool IsRetryExpired(ulong queuedAtSlot, ulong currentSlot) => currentSlot > queuedAtSlot + MaxPendingRetryAgeEpochs * spec.SlotsPerEpoch;

    /// <summary>Drops held envelopes at or below the finalized slot or past their age, then retries each envelope waiting on its data or the engine.</summary>
    /// <remarks>An envelope's slot is its own unverified claim, so the per-root and total caps and the age bound are what bound a forged one.</remarks>
    private async Task DrainPendingEnvelopesAsync(CancellationToken token)
    {
        ulong finalizedSlot = FinalizedSlot;
        ulong currentSlot = slotClock.CurrentSlot;
        bool Expired(ParkedEnvelope parked) => (parked.Envelope.Message!.Payload?.SlotNumber ?? 0) <= finalizedSlot || IsRetryExpired(parked.QueuedAtSlot, currentSlot);
        _pendingEnvelopesByBlock.RemoveWhere(Expired);
        _pendingEnvelopeRetry.RemoveWhere(Expired);

        foreach (ParkedEnvelope parked in _pendingEnvelopeRetry.Snapshot())
        {
            await RetryParkedEnvelopeAsync(parked, token, retryingOnColumns: false);
        }
    }

    private async Task RetryParkedEnvelopeAsync(ParkedEnvelope parked, CancellationToken token, bool retryingOnColumns)
    {
        ExecutionPayloadEnvelopeImportResult? result = await ImportEnvelopeAsync(parked.Envelope, token, parked.Source, retryingOnColumns);
        if (result is not (ExecutionPayloadEnvelopeImportResult.DataUnavailable or ExecutionPayloadEnvelopeImportResult.EngineUnavailable))
        {
            _pendingEnvelopeRetry.Remove(parked);
        }
    }

    /// <summary>Queues an envelope that waits on its blob data or on the engine for a retry on the slot tick.</summary>
    /// <param name="source">The req/resp peer that served the envelope, penalized if a retry finds it invalid.</param>
    private void OnEnvelopeDataUnavailable(SignedExecutionPayloadEnvelope envelope, ExecutionPayloadEnvelopeImportResult result, bool retryingOnColumns, CancellationToken token, IBeaconSyncPeer? source)
    {
        Hash256 root = envelope.Message!.BeaconBlockRoot!;
        _pendingEnvelopeRetry.Add(root, envelope, source, slotClock.CurrentSlot);
        if (result == ExecutionPayloadEnvelopeImportResult.DataUnavailable && !retryingOnColumns)
        {
            WatchColumns(root, gloas: true);
            if (_columnRecovery.TryGetValue(root, out ColumnRecovery? recovery))
            {
                RecoverColumns(root, recovery, slotClock.CurrentSlot, token);
            }
        }
    }

    /// <summary>Remembers an imported Gloas block whose bid commits blobs, so <see cref="RecoverGloasColumns"/> fetches its missing sampled columns.</summary>
    /// <returns>The new entry, or <c>null</c> when the block demands no columns or is tracked already.</returns>
    private ColumnRecovery? TrackColumnRecovery(Hash256 root, ForkedSignedBeaconBlock block)
    {
        if (block is not ForkedSignedBeaconBlock.OfGloas { Block.Message.Body.SignedExecutionPayloadBid.Message: { BlobKzgCommitments.Length: > 0 } bid }
            || _columnRecovery.ContainsKey(root))
        {
            return null;
        }

        if (_columnRecovery.Count >= MaxColumnRecoveryBlocks)
        {
            // The newest block is the one whose envelope is due next, so the oldest gives way.
            Hash256? oldest = null;
            ulong oldestSlot = ulong.MaxValue;
            foreach ((Hash256 tracked, ColumnRecovery recovery) in _columnRecovery)
            {
                if (recovery.Bid.Slot < oldestSlot)
                {
                    oldest = tracked;
                    oldestSlot = recovery.Bid.Slot;
                }
            }

            _columnRecovery.Remove(oldest!);
        }

        ColumnRecovery added = new(bid, slotClock.CurrentSlot, LastAttemptSlot: ulong.MaxValue, new RangeSync.ColumnFetchRotation(slotClock));
        _columnRecovery[root] = added;
        return added;
    }

    /// <summary>
    /// Fetches by root, at most once per block per slot, the sampled columns still missing for each imported Gloas block that
    /// commits blobs, until every one is held, the block falls to finality or it has waited past <see cref="MaxPendingRetryAgeEpochs"/>.
    /// The first attempt is made when the block imports; the slot tick retries.
    /// </summary>
    /// <remarks>
    /// Gossip candidates carry no source peer to bound, so a flood can take every candidate place of a column, or every
    /// candidate can fail verification; the columns then arrive only by DataColumnSidecarsByRoot (gloas/p2p-interface.md).
    /// </remarks>
    private void RecoverGloasColumns(CancellationToken token)
    {
        if (_columnRecovery.Count == 0)
        {
            return;
        }

        ulong finalizedSlot = FinalizedSlot;
        ulong currentSlot = slotClock.CurrentSlot;
        List<KeyValuePair<Hash256, ColumnRecovery>> tracked = [.. _columnRecovery];
        foreach ((Hash256 root, ColumnRecovery recovery) in tracked)
        {
            if (recovery.Bid.Slot <= finalizedSlot || IsRetryExpired(recovery.QueuedAtSlot, currentSlot))
            {
                _columnRecovery.Remove(root);
                continue;
            }

            RecoverColumns(root, recovery, currentSlot, token);
        }
    }

    private void RecoverColumns(Hash256 root, ColumnRecovery recovery, ulong currentSlot, CancellationToken token)
    {
        if (recovery.LastAttemptSlot == currentSlot)
        {
            return;
        }

        if (StartColumnFetch(root, fetchToken => rangeSync.FetchGloasColumnsByRootAsync(root, recovery.Bid, recovery.Rotation, fetchToken), token))
        {
            _columnRecovery[root] = recovery with { LastAttemptSlot = currentSlot };
        }
    }

    /// <summary>
    /// Imports an execution payload envelope and acts on the verdict. Once its payload is recorded it is pooled for
    /// ExecutionPayloadEnvelopesByRange and ByRoot, its (block root, builder index) is marked seen for gossip, and every
    /// pending block that waits on it as its full parent is re-driven at once: the next slot's block needs that payload within the slot.
    /// </summary>
    /// <remarks>
    /// An envelope for a block not imported yet is held until that block imports, and one waiting on its data or the engine
    /// is retried on the slot tick. An invalid envelope penalizes the req/resp peer that served it. A verdict this method does
    /// not know is dropped, neither marked seen nor pooled, so a result added later fails closed.
    /// </remarks>
    /// <param name="source">The req/resp peer that served the envelope; <c>null</c> for gossip.</param>
    /// <param name="retryingOnColumns">Whether this import was woken by the columns it waited for, so a repeat deferral waits for the slot tick.</param>
    /// <returns>The verdict, or <c>null</c> when the envelope carries no message or block root, or its import failed on a local fault, and it was dropped.</returns>
    internal async Task<ExecutionPayloadEnvelopeImportResult?> ImportEnvelopeAsync(SignedExecutionPayloadEnvelope envelope, CancellationToken token, IBeaconSyncPeer? source = null, bool retryingOnColumns = false)
    {
        if (envelope.Message is not { BeaconBlockRoot: not null } message)
        {
            return null;
        }

        ExecutionPayloadEnvelopeImportResult result;
        try
        {
            result = _importer!.ImportEnvelope(envelope);
        }
        catch (Exception e)
        {
            // A local fault is not the sender's, and it must not stop the worker every other message goes through.
            if (_logger.IsError) _logger.Error($"Dropping the execution payload envelope for beacon block {message.BeaconBlockRoot}: its import failed", e);
            return null;
        }

        switch (result)
        {
            case ExecutionPayloadEnvelopeImportResult.Valid or ExecutionPayloadEnvelopeImportResult.Optimistic:
                Hash256 blockRoot = message.BeaconBlockRoot!;
                envelopePool?.Add(blockRoot, envelope);
                gossipRouter.MarkEnvelopeSeen(blockRoot, message.BuilderIndex);
                await OnPayloadRecordedAsync(blockRoot, token);
                break;
            case ExecutionPayloadEnvelopeImportResult.AlreadyKnown:
                gossipRouter.MarkEnvelopeSeen(message.BeaconBlockRoot!, message.BuilderIndex);
                break;
            case ExecutionPayloadEnvelopeImportResult.UnknownBlock:
                _pendingEnvelopesByBlock.Add(message.BeaconBlockRoot!, envelope, source, slotClock.CurrentSlot);
                break;
            case ExecutionPayloadEnvelopeImportResult.DataUnavailable or ExecutionPayloadEnvelopeImportResult.EngineUnavailable:
                OnEnvelopeDataUnavailable(envelope, result, retryingOnColumns, token, source);
                break;
            case ExecutionPayloadEnvelopeImportResult.Invalid:
                source?.ReportFailure(PeerFailureReason.ProtocolViolation, $"Execution payload envelope for beacon block {message.BeaconBlockRoot} is invalid");
                break;
            default:
                if (_logger.IsWarn) _logger.Warn($"Dropping the execution payload envelope for beacon block {message.BeaconBlockRoot}: unhandled import result {result}");
                break;
        }

        return result;
    }

    /// <summary>Re-drives every block in the retry set that waits on the payload just recorded for <paramref name="blockRoot"/>.</summary>
    private async Task OnPayloadRecordedAsync(Hash256 blockRoot, CancellationToken token)
    {
        // The execution layer now holds the payload, and only forkchoiceUpdated makes it the head.
        _importedSinceHeadStep = true;
        List<ForkedSignedBeaconBlock>? children = null;
        foreach (PendingRetry pending in _pendingRetry.Values)
        {
            if (pending.Block.ParentRoot == blockRoot)
            {
                (children ??= []).Add(pending.Block);
            }
        }

        if (children is not null)
        {
            foreach (ForkedSignedBeaconBlock child in children)
            {
                await ImportBlockAsync(child, token);
            }
        }
    }

    private static bool IsPayloadRecorded(ExecutionPayloadEnvelopeImportResult? result) =>
        result is ExecutionPayloadEnvelopeImportResult.Valid or ExecutionPayloadEnvelopeImportResult.Optimistic or ExecutionPayloadEnvelopeImportResult.AlreadyKnown;

    /// <summary>Imports the envelopes held for <paramref name="blockRoot"/> in arrival order until one records its payload; the rest are dropped.</summary>
    /// <returns>Whether an envelope recorded the payload or found it recorded already.</returns>
    private async Task<bool> ImportHeldEnvelopesAsync(Hash256 blockRoot, CancellationToken token)
    {
        if (_pendingEnvelopesByBlock.Take(blockRoot) is not { } held)
        {
            return false;
        }

        foreach (ParkedEnvelope parked in held)
        {
            if (IsPayloadRecorded(await ImportEnvelopeAsync(parked.Envelope, token, parked.Source)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Recovers the envelope of <paramref name="parentRoot"/>, a block fork choice holds whose payload a parked full child needs:
    /// from the envelopes held for it, else by ExecutionPayloadEnvelopesByRoot from up to <see cref="MaxBackfillPeersPerRequest"/>
    /// peers, at most once per root per slot. Each envelope found is imported as a <see cref="FetchedEnvelopeItem"/>, which
    /// re-drives the parked children.
    /// </summary>
    /// <remarks>
    /// gloas/fork-choice.md <c>get_forkchoice_store</c> starts with <c>payloads = {}</c>, so the anchor's payload is recovered this
    /// way too when its first child builds on it. An envelope for a root other than the one asked for penalizes its peer.
    /// </remarks>
    /// <returns>Whether an envelope recorded the payload or found it recorded already.</returns>
    private async Task<bool> RecoverParentEnvelopeAsync(Hash256 parentRoot, CancellationToken token)
    {
        if (await ImportHeldEnvelopesAsync(parentRoot, token))
        {
            return true;
        }

        // The held envelope waits on its data or the engine, which another copy would not change.
        if (_pendingEnvelopeRetry.Contains(parentRoot))
        {
            return false;
        }

        ulong currentSlot = slotClock.CurrentSlot;
        if (_envelopeRequestedAtSlot.TryGet(parentRoot, out ulong requestedAt) && requestedAt == currentSlot)
        {
            return false;
        }

        _envelopeRequestedAtSlot.Set(parentRoot, currentSlot);
        IReadOnlyList<IBeaconSyncPeer> peers = peerPool.GetBestPeers(0);
        for (int i = 0; i < peers.Count && i < MaxBackfillPeersPerRequest; i++)
        {
            IBeaconSyncPeer peer = peers[i];
            IReadOnlyList<SignedExecutionPayloadEnvelope> envelopes;
            try
            {
                envelopes = await peer.RequestExecutionPayloadEnvelopesByRootAsync([parentRoot], token);
            }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
            {
                peer.ReportFailure(PeerFailureClassifier.Classify(e), $"Execution-payload-envelopes-by-root for {parentRoot} failed: {e.Message}");
                continue;
            }

            foreach (SignedExecutionPayloadEnvelope envelope in envelopes)
            {
                if (envelope.Message?.BeaconBlockRoot != parentRoot)
                {
                    peer.ReportFailure(PeerFailureReason.ProtocolViolation, $"Execution-payload-envelopes-by-root for {parentRoot} returned an envelope for another block");
                    continue;
                }

                if (IsPayloadRecorded(await ImportEnvelopeItemAsync(new FetchedEnvelopeItem(envelope, peer), token)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private Task<ExecutionPayloadEnvelopeImportResult?> ImportEnvelopeItemAsync(EnvelopeItem item, CancellationToken token) =>
        ImportEnvelopeAsync(item.Envelope, token, item.Source);

    private async Task OnImportedAsync(Hash256 root, ulong slot, CancellationToken token)
    {
        if (slot > _syncTip.Slot)
        {
            _syncTip = new Tip(root, slot);
        }

        _importedSinceHeadStep = true;
        LogSyncProgress(slot);

        // Before the held children, so a full child finds its parent's payload recorded instead of asking a peer for it.
        await ImportHeldEnvelopesAsync(root, token);
        if (_pendingByParent.Remove(root, out List<ForkedSignedBeaconBlock>? children))
        {
            _pendingCount -= children.Count;
            foreach (ForkedSignedBeaconBlock child in children)
            {
                if (_heldForPayload.Count > 0)
                {
                    _heldForPayload.Remove(child.ComputeMessageRoot());
                }

                await ImportBlockAsync(child, token);
            }
        }

        if (++_importsSinceHeadStep >= HeadStepImportInterval)
        {
            await RunHeadStepAsync(token);
        }
    }

    /// <summary>
    /// Full gossip validation, then import. Checks (in order): not already known, past the
    /// finalized slot, no block with a valid signature seen for its (slot, proposer), expected proposer per the lookahead.
    /// The proposer signature is verified by the state transition during the immediate import
    /// (the import runs with <c>verifySignatures: true</c> right below), so no separate
    /// pre-verification pass is needed.
    /// </summary>
    internal async Task ProcessGossipBlockAsync(ForkedSignedBeaconBlock block, CancellationToken token)
    {
        IBlockImporter importer = _importer!;
        Hash256 root = block.ComputeMessageRoot();
        if (importer.IsKnown(root))
        {
            return;
        }

        if (_lastHead is { } head && block.Slot <= BeaconStateAccessors.ComputeStartSlotAtEpoch(head.Finalized.Epoch))
        {
            return;
        }

        if (gossipRouter.IsProposalSeen(block.Slot, block.ProposerIndex))
        {
            if (_logger.IsDebug) _logger.Debug($"Ignoring repeat gossip proposal for slot {block.Slot} by proposer {block.ProposerIndex}");
            return;
        }

        if (!importer.IsExpectedProposer(block))
        {
            if (_logger.IsWarn) _logger.Warn($"Dropping gossip block at slot {block.Slot} with unexpected proposer {block.ProposerIndex}");
            return;
        }

        // The parent's import drains this child, so the parent is not fetched again.
        if (IsWaitingForPayload(block.ParentRoot))
        {
            HoldForWaitingParent(block);
            return;
        }

        if (!importer.IsKnown(block.ParentRoot))
        {
            // While far behind, range sync will deliver the parent chain anyway - just hold the
            // block; in steady state fetch the missing ancestors by root.
            if (_syncTip.Slot + MaxBackfillDepth < slotClock.CurrentSlot)
            {
                QueuePendingGossipBlock(block);
            }
            else if (TryTakeBackfill(block))
            {
                await BackfillAndImportAsync(block, token);
                Hash256 parent = block.ParentRoot;
                // A parent that neither imported nor waits for a retry may be fetched again, within the slot's spent budget.
                if (!importer.IsKnown(parent) && !_pendingRetry.ContainsKey(parent) && !IsWaitingForPayload(parent))
                {
                    _backfilledParents.Remove(parent);
                }
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Holding gossip block at slot {block.Slot} for unknown parent {block.ParentRoot}: {(_backfilledParents.Contains(block.ParentRoot) ? "that parent waits for a retry" : "backfill budget for this slot spent")}");
                HoldRefusedBackfill(block);
            }

            return;
        }

        await ImportBlockAsync(block, token);
    }

    /// <summary>Whether a by-root backfill may start for <paramref name="block"/>: one per unknown parent and <see cref="MaxBackfillsPerSlot"/> per wall-clock slot.</summary>
    /// <remarks>
    /// The block's proposer signature is checked only once its parent chain is fetched and it imports, so without this bound each
    /// forged unknown-parent block would buy an outbound request. The key is the parent, not the (slot, proposer): an unsigned
    /// block naming the real proposer must not take the backfill of the real block. A parent released after a failed fetch keeps
    /// its share of the budget, so failed fetches cannot buy more requests.
    /// </remarks>
    private bool TryTakeBackfill(ForkedSignedBeaconBlock block)
    {
        ulong currentSlot = slotClock.CurrentSlot;
        if (currentSlot != _backfillSlot)
        {
            _backfillSlot = currentSlot;
            _backfilledParents.Clear();
            _backfillsThisSlot = 0;
        }

        if (_backfillsThisSlot >= MaxBackfillsPerSlot || !_backfilledParents.Add(block.ParentRoot))
        {
            return false;
        }

        _backfillsThisSlot++;
        return true;
    }

    /// <summary>Holds a block refused a backfill until its parent imports by another route, evicting the oldest such block past <see cref="MaxHeldRefusedBackfills"/>.</summary>
    /// <remarks>
    /// The cap keeps forged blocks from filling the queue shared with blocks held for a parent payload. Under a sustained flood the
    /// real block can still be evicted; a later block's backfill or the range-sync feed then fetches it.
    /// </remarks>
    private void HoldRefusedBackfill(ForkedSignedBeaconBlock block)
    {
        if (_pendingByParent.TryGetValue(block.ParentRoot, out List<ForkedSignedBeaconBlock>? held) && held.Contains(block))
        {
            return;
        }

        if (_heldForBackfill.Count >= MaxHeldRefusedBackfills)
        {
            ForkedSignedBeaconBlock oldest = _heldForBackfill.Dequeue();
            // An entry its parent's import already drained holds no queue slot.
            if (_pendingByParent.TryGetValue(oldest.ParentRoot, out List<ForkedSignedBeaconBlock>? siblings) && siblings.Remove(oldest))
            {
                _pendingCount--;
                if (siblings.Count == 0)
                {
                    _pendingByParent.Remove(oldest.ParentRoot);
                }
            }
        }

        if (QueuePendingGossipBlock(block))
        {
            _heldForBackfill.Enqueue(block);
        }
    }

    private bool QueuePendingGossipBlock(ForkedSignedBeaconBlock block)
    {
        if (_pendingCount >= MaxPendingGossipBlocks)
        {
            return false;
        }

        Hash256 parent = block.ParentRoot;
        if (!_pendingByParent.TryGetValue(parent, out List<ForkedSignedBeaconBlock>? siblings))
        {
            _pendingByParent[parent] = siblings = [];
        }

        siblings.Add(block);
        _pendingCount++;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="blockRoot"/> is a Gloas block waiting in the retry set for its own parent's payload, or a
    /// block held for one; specs/gloas/p2p-interface.md <c>beacon_block</c> lets a client queue a block until the parent payload is retrieved.
    /// </summary>
    private bool IsWaitingForPayload(Hash256 blockRoot) =>
        // A Gloas block is only ever retried for its parent's payload.
        (_pendingRetry.TryGetValue(blockRoot, out PendingRetry parked) && parked.Block is ForkedSignedBeaconBlock.OfGloas)
        || _heldForPayload.Contains(blockRoot);

    /// <summary>Holds <paramref name="block"/> until its parent, found by <see cref="IsWaitingForPayload"/>, imports.</summary>
    /// <remarks>
    /// The block is held only once the importer defers it too, which it does only after its proposer and proposer signature
    /// check out, and its (slot, proposer) is then marked seen: the queue is bounded, so unsigned or repeated proposals must
    /// not be able to crowd out the real block. The seen gate covers fetched ancestors too, or one equivocating proposer
    /// could fill the queue through forged children naming each of its blocks; such an ancestor imports once range sync or
    /// a later by-root fetch delivers it after its parent. A block the full queue cannot take is not marked seen.
    /// </remarks>
    /// <returns>Whether the block is held.</returns>
    private bool HoldForWaitingParent(ForkedSignedBeaconBlock block)
    {
        Hash256 root = block.ComputeMessageRoot();
        if (_pendingCount >= MaxPendingGossipBlocks
            || gossipRouter.IsProposalSeen(block.Slot, block.ProposerIndex)
            || _importer!.Import(block, root, verifySignatures: true) != BlockImportResult.ParentPayloadUnverified)
        {
            return false;
        }

        QueuePendingGossipBlock(block);
        gossipRouter.MarkProposalSeen(block.Slot, block.ProposerIndex);
        _heldForPayload.Add(root);
        return true;
    }

    /// <summary>Holds <paramref name="chain"/> from index <paramref name="from"/> down to its gossip block at index 0, stopping at the first block not held.</summary>
    private void HoldChainForWaitingParent(List<ForkedSignedBeaconBlock> chain, int from)
    {
        for (int i = from; i >= 0; i--)
        {
            if (!HoldForWaitingParent(chain[i]))
            {
                return;
            }
        }
    }

    /// <summary>Fetches the unknown parent chain of a gossip block by root (bounded depth), then imports oldest-first.</summary>
    private async Task BackfillAndImportAsync(ForkedSignedBeaconBlock block, CancellationToken token)
    {
        IBlockImporter importer = _importer!;
        List<ForkedSignedBeaconBlock> chain = [block];
        Hash256 parent = block.ParentRoot;
        while (!importer.IsKnown(parent))
        {
            if (IsWaitingForPayload(parent))
            {
                HoldChainForWaitingParent(chain, chain.Count - 1);
                return;
            }

            if (chain.Count > MaxBackfillDepth)
            {
                if (_logger.IsDebug) _logger.Debug($"Giving up on gossip block at slot {block.Slot}: ancestor chain exceeds {MaxBackfillDepth} unknown blocks");
                return;
            }

            ForkedSignedBeaconBlock? fetched = await FetchBlockByRootAsync(parent, token);
            if (fetched is null)
            {
                if (_logger.IsDebug) _logger.Debug($"Giving up on gossip block at slot {block.Slot}: no peer returned ancestor {parent}");
                return;
            }

            chain.Add(fetched);
            parent = fetched.ParentRoot;
        }

        for (int i = chain.Count - 1; i >= 0; i--)
        {
            BlockImportResult result = await ImportBlockAsync(chain[i], token);
            if (result == BlockImportResult.ParentPayloadUnverified && i > 0 && _pendingRetry.ContainsKey(chain[i].ComputeMessageRoot()))
            {
                HoldChainForWaitingParent(chain, i - 1);
                return;
            }

            if (result is not (BlockImportResult.Imported or BlockImportResult.AlreadyKnown))
            {
                return;
            }
        }
    }

    private async Task<ForkedSignedBeaconBlock?> FetchBlockByRootAsync(Hash256 root, CancellationToken token)
    {
        IReadOnlyList<IBeaconSyncPeer> peers = peerPool.GetBestPeers(0);
        for (int i = 0; i < peers.Count && i < MaxBackfillPeersPerRequest; i++)
        {
            IBeaconSyncPeer peer = peers[i];
            IReadOnlyList<ForkedSignedBeaconBlock> blocks;
            try
            {
                blocks = await peer.RequestBlocksByRootAsync([root], token);
            }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
            {
                // Includes per-request timeouts, which cancel the request without cancelling the sync.
                peer.ReportFailure(PeerFailureClassifier.Classify(e), $"Blocks-by-root for {root} failed: {e.Message}");
                continue;
            }

            foreach (ForkedSignedBeaconBlock block in blocks)
            {
                if (block.ComputeMessageRoot() == root)
                {
                    return block;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The fork-choice head step: recompute the head, send <c>forkchoiceUpdated</c>
    /// (safe = justified, finalized = finalized), handle an INVALID verdict by invalidating and
    /// retrying once, react to finality advances, and refresh the advertised status.
    /// </summary>
    internal async Task RunHeadStepAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _importedSinceHeadStep = false;
        _importsSinceHeadStep = 0;

        IBlockImporter importer = _importer!;
        HeadView head = importer.ComputeHead();
        if (_logger.IsDebug) _logger.Debug($"Head step: head {head.HeadRoot} at slot {head.HeadSlot}, sync tip slot {_syncTip.Slot}, justified epoch {head.Justified.Epoch}, finalized epoch {head.Finalized.Epoch}, wall slot {slotClock.CurrentSlot}");
        if (head.HeadExecutionHash is { } headExec)
        {
            PayloadStatusV1 status = await ForkchoiceUpdatedAsync(head, headExec);
            if (status.Status == PayloadStatus.Invalid)
            {
                if (_logger.IsWarn) _logger.Warn($"Execution layer reported head {head.HeadRoot} INVALID (latest valid hash {status.LatestValidHash}); invalidating and re-running fork choice");
                importer.OnInvalidExecutionPayload(head.HeadRoot, status.LatestValidHash);
                head = importer.ComputeHead();
                if (head.HeadExecutionHash is { } retryExec)
                {
                    status = await ForkchoiceUpdatedAsync(head, retryExec);
                }
            }

            TrackExecutionSyncTransition(status);
        }

        if (_lastHead is { } previous && head.Finalized.Epoch > previous.Finalized.Epoch)
        {
            if (_logger.IsInfo) _logger.Info($"FINALIZED epoch={head.Finalized.Epoch} root={head.Finalized.Root}");
            importer.OnFinalized(head.Finalized);
        }

        _lastHead = head;
        Metrics.BeaconChainHeadSlot = head.HeadSlot;
        Metrics.BeaconChainHeadSlotDelay = (long)slotClock.CurrentSlot - (long)head.HeadSlot;
        Metrics.BeaconChainFinalizedEpoch = head.Finalized.Epoch;
        Metrics.BeaconChainJustifiedEpoch = head.Justified.Epoch;
        Metrics.BeaconChainElInSync = _elInSync ? 1 : 0;
        statusHolder.JustifiedRoot = head.Justified.Root;
        statusHolder.ExecutionInSync = _elInSync;
        statusHolder.CurrentStatus = new StatusMessageV2
        {
            ForkDigest = _currentDigest,
            FinalizedRoot = head.Finalized.Root,
            FinalizedEpoch = head.Finalized.Epoch,
            HeadRoot = head.HeadRoot,
            HeadSlot = head.HeadSlot,
            EarliestAvailableSlot = EarliestAvailableSlot(),
        };

        // A replay near the wall clock runs before the libp2p host starts, and topics exist only once it has.
        if (!GossipStarted && p2p?.LocalPeerId is not null && head.HeadSlot + GossipStartDistanceSlots >= slotClock.CurrentSlot)
        {
            StartGossip();
        }
    }

    /// <summary>Points the execution layer at the anchor payload through <see cref="ForkchoiceUpdatedAsync"/>, so the first head step does not repeat an unchanged state.</summary>
    internal Task<PayloadStatusV1> KickExecutionAsync(Hash256 anchorRoot)
    {
        CheckpointRef anchor = new(spec.GetEpoch(_anchorSlot), anchorRoot);
        return ForkchoiceUpdatedAsync(new HeadView(anchorRoot, _anchorSlot, _anchorExecutionHash, null, null, anchor, anchor), _anchorExecutionHash);
    }

    /// <summary>Sends <c>forkchoiceUpdated</c> unless the same head, safe and finalized hashes were sent within <see cref="ForkchoiceResendInterval"/>.</summary>
    /// <remarks>
    /// The Engine API asks for the call when the fork choice state changes; an unchanged repeat each slot tells the EL nothing.
    /// It is still resent at that interval, as the EL treats a CL that sends neither this nor <c>newPayload</c> for a while as gone.
    /// </remarks>
    private async Task<PayloadStatusV1> ForkchoiceUpdatedAsync(HeadView head, Hash256 headExec)
    {
        ForkchoiceHashes sent = new(headExec, head.JustifiedExecutionHash ?? headExec, head.FinalizedExecutionHash ?? _anchorExecutionHash);
        long now = slotClock.UnixMilliseconds;
        if (_lastForkchoice is { } last && last.Hashes == sent && last.Status.Status != PayloadStatus.Invalid
            && now - last.SentAtMs < (long)ForkchoiceResendInterval.TotalMilliseconds)
        {
            return last.Status;
        }

        PayloadStatusV1 status = await engine.ForkchoiceUpdated(sent.Head, sent.Safe, sent.Finalized);
        _lastForkchoice = (sent, now, status);
        return status;
    }

    private readonly record struct ForkchoiceHashes(Hash256 Head, Hash256 Safe, Hash256 Finalized);

    private void TrackExecutionSyncTransition(PayloadStatusV1 status)
    {
        if (!_elInSync && status.Status == PayloadStatus.Valid)
        {
            _elInSync = true;
            if (_logger.IsInfo) _logger.Info("Execution layer is in sync with the beacon chain head (forkchoiceUpdated returned VALID)");
        }
        else if (_elInSync && status.Status == PayloadStatus.Syncing)
        {
            _elInSync = false;
            if (_logger.IsWarn) _logger.Warn("Execution layer fell back to SYNCING");
        }
    }

    /// <summary>Per-slot work: fork-choice tick, head step, BPO/fork digest rotation, and the once-per-epoch status log.</summary>
    internal async Task ProcessSlotAsync(ulong slot, CancellationToken token)
    {
        gossipRouter.ReleaseDueMessages();
        _importer!.OnSlotTick(slot);
        await RunHeadStepAsync(token);
        if (_lastHead is { } stalled && HasHeadStalled(stalled.HeadSlot, slot) && GossipStarted && stalled.HeadSlot + GossipStartDistanceSlots < slot)
        {
            ResumeRangeSyncFromHead(slot);
        }

        await DrainPendingRetriesAsync(token);
        RecoverGloasColumns(token);
        await DrainPendingEnvelopesAsync(token);
        ReleaseIdleColumnWatches();

        ulong epoch = spec.GetEpoch(slot);
        if (_nextRotation is { } rotation && epoch >= rotation.Epoch)
        {
            _currentDigest = rotation.Digest;
            _nextRotation = GossipTopics.NextRotation(spec, rotation.Epoch);
            discovery?.UpdateLocalEnr();
            if (_logger.IsInfo) _logger.Info($"Rotated beacon gossip fork digest to 0x{Convert.ToHexStringLower(rotation.Digest)} at epoch {epoch}");
        }

        // Empty until StartGossip has started the routers.
        if (_gossipDigests.Length > 0)
        {
            TryStartColumnGossip();
            ReconcileGossipDigests(epoch);
        }

        if (epoch > _statusLogEpoch && _lastHead is { } head && _logger.IsInfo)
        {
            _statusLogEpoch = epoch;
            _logger.Info($"Beacon chain: head slot {head.HeadSlot} ({head.HeadRoot}), finalized epoch {head.Finalized.Epoch}, peers {peerManager?.PeerCount ?? 0}/{config.TargetPeerCount}, EL {(_elInSync ? "in sync" : "syncing")}");
        }
    }

    /// <summary>Routes the gossip router's blocks and envelopes into the work channel and its votes and slashings into the vote channel; gossip overflow is droppable.</summary>
    internal void RouteGossipEvents()
    {
        gossipRouter.BeaconBlockReceived += block => _work.Writer.TryWrite(new GossipBlockItem(block));
        gossipRouter.AggregateAndProofReceived += aggregate => QueueVote(new GossipAggregateItem(aggregate));
        gossipRouter.GloasAggregateAndProofReceived += aggregate => QueueVote(new GossipGloasAggregateItem(aggregate));
        gossipRouter.AttesterSlashingReceived += slashing => QueueVote(new GossipAttesterSlashingItem(slashing));
        gossipRouter.GloasAttesterSlashingReceived += slashing => QueueVote(new GossipGloasAttesterSlashingItem(slashing));
        gossipRouter.ExecutionPayloadEnvelopeReceived += envelope => _work.Writer.TryWrite(new GossipEnvelopeItem(envelope));
        gossipRouter.PayloadAttestationMessageReceived += vote =>
        {
            if (!QueueVote(new GossipPayloadAttestationItem(vote)))
            {
                gossipRouter.ReleasePayloadAttestation(vote);
            }
        };
    }

    private bool QueueVote(WorkItem vote)
    {
        if (_votes.Writer.TryWrite(new QueuedVote(vote, Volatile.Read(ref _newestSlotTick))))
        {
            WakeForVotes();
            return true;
        }

        Metrics.BeaconChainGossipDropped++;
        return false;
    }

    // A wake refused by a full work channel is not needed: the worker has a pass to run, and each pass reads the votes.
    private void WakeForVotes()
    {
        if (Interlocked.Exchange(ref _voteWakeQueued, 1) == 0)
        {
            _work.Writer.TryWrite(VoteWakeItem.Instance);
        }
    }

    private void StartGossip() => StartGossip(p2p!.GetTopic);

    /// <summary>
    /// Subscribes the gossip topics of every digest <see cref="GossipTopics.DigestsAround"/> names for the wall-clock epoch, and the
    /// column subnets this node samples, then routes the events into the work channel; gossip overflow is droppable.
    /// </summary>
    /// <remarks>Column subnets need the custody discovery advertises; until discovery has one, every slot tick retries them.</remarks>
    internal void StartGossip(Func<string, ITopic> getTopic)
    {
        GossipStarted = true;
        _getTopic = getTopic;
        RouteGossipEvents();
        gossipRouter.Start(getTopic, _currentDigest);
        if (!TryStartColumnGossip() && columnRouter is not null && _logger.IsWarn)
        {
            _logger.Warn("No local column custody yet; data column sidecar subnets are subscribed once discovery has one");
        }

        ReconcileGossipDigests(slotClock.CurrentEpoch);
        if (_logger.IsInfo) _logger.Info($"Within {GossipStartDistanceSlots} slots of the wall clock - gossip following started");
    }

    private bool TryStartColumnGossip()
    {
        if (_columnGossipStarted)
        {
            return true;
        }

        if (columnRouter is null || _custody.Current is not { } custody)
        {
            return false;
        }

        // fulu/das-core.md custody sampling: availability needs every sampled column, so its subnet is subscribed, not only the custodied ones.
        SortedSet<ulong> subnets = [];
        foreach (ulong column in custody.SampledColumns)
        {
            subnets.Add(CustodyGroups.ComputeSubnetForDataColumnSidecar(column));
        }

        columnRouter.Start(_getTopic!, _currentDigest, [.. subnets]);
        _columnGossipStarted = true;
        return true;
    }

    /// <summary>Subscribes both gossip routers to the digests of <see cref="GossipTopics.DigestsAround"/> at <paramref name="epoch"/> and unsubscribes the rest.</summary>
    /// <remarks>An epoch older than the last one applied is ignored: a tick queued behind imports must not undo the window gossip started at.</remarks>
    internal void ReconcileGossipDigests(ulong epoch)
    {
        if (_gossipDigests.Length > 0 && epoch < _reconciledEpoch)
        {
            return;
        }

        _reconciledEpoch = epoch;
        (byte[] Digest, bool Gloas)[] wanted = GossipTopics.DigestsAround(spec, epoch);
        foreach ((byte[] digest, bool _) in _gossipDigests)
        {
            if (!Array.Exists(wanted, w => w.Digest.AsSpan().SequenceEqual(digest)))
            {
                gossipRouter.UnsubscribeDigest(digest);
                if (_columnGossipStarted)
                {
                    columnRouter!.UnsubscribeDigest(digest);
                }
            }
        }

        foreach ((byte[] digest, bool _) in wanted)
        {
            gossipRouter.SubscribeDigest(digest);
            if (_columnGossipStarted)
            {
                columnRouter!.SubscribeDigest(digest);
            }
        }

        _gossipDigests = wanted;
    }

    private async Task PumpSlotTicksAsync(CancellationToken token)
    {
        await foreach (ulong slot in slotClock.SlotTicks(token))
        {
            await EnqueueSlotTickAsync(slot, token);
        }
    }

    internal ValueTask EnqueueSlotTickAsync(ulong slot, CancellationToken token)
    {
        Volatile.Write(ref _newestSlotTick, slot);
        return _work.Writer.WriteAsync(new SlotTickItem(slot), token);
    }

    /// <summary>Feeds range-synced blocks into the work channel, re-running as the wall clock outpaces the sync tip.</summary>
    internal async Task RunRangeSyncFeedAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            CancellationToken restart = Volatile.Read(ref _rangeSyncRestart).Token;
            try
            {
                await FeedRangeSyncRoundAsync(token);
            }
            catch (OperationCanceledException) when (restart.IsCancellationRequested && !token.IsCancellationRequested)
            {
            }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
            {
                // The feed must survive network-level failures; the next round restarts from the tip.
                if (_logger.IsDebug) _logger.Debug($"Range sync round failed and will be retried: {e.Message}");
            }

            // Caught up (or briefly stalled): in steady state gossip keeps the tip moving and this
            // loop only re-checks for gaps once per slot.
            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(token, restart);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(spec.SecondsPerSlot), wait.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>Records the head slot at this slot tick; whether it has not moved for an epoch of ticks, so a catch-up still advancing is left running.</summary>
    private bool HasHeadStalled(ulong headSlot, ulong slot)
    {
        if (_headSlotSeen is not { } seen || seen.HeadSlot != headSlot)
        {
            _headSlotSeen = seen = (headSlot, slot);
        }

        return slot >= seen.SinceSlot + spec.SlotsPerEpoch;
    }

    /// <summary>
    /// Restarts range sync from the fork-choice head, at most once per epoch: ends the round in flight and the wait after it,
    /// and moves the sync tip to the head when it is on another block.
    /// </summary>
    /// <remarks>
    /// A round follows the wall clock past blocks that did not import, which then fail as <see cref="BlockImportResult.UnknownParent"/>,
    /// so a block the head waits on is fetched again only by a round that starts from the head.
    /// </remarks>
    private void ResumeRangeSyncFromHead(ulong slot)
    {
        if (_lastHead is not { } head || (_rangeSyncResumedAtSlot is { } resumedAt && slot < resumedAt + spec.SlotsPerEpoch))
        {
            return;
        }

        _rangeSyncResumedAtSlot = slot;
        if (_syncTip.Root != head.HeadRoot)
        {
            _syncTip = new Tip(head.HeadRoot, head.HeadSlot);
        }

        // Cancelled asynchronously, so the round's continuations do not run on the worker.
        _ = Interlocked.Exchange(ref _rangeSyncRestart, new CancellationTokenSource()).CancelAsync();
        if (_logger.IsInfo) _logger.Info($"Beacon head at slot {head.HeadSlot} is stuck ({slot - Math.Min(slot, head.HeadSlot)} behind wall slot {slot}); resuming range sync from the head");
    }

    /// <summary>Runs one range-sync round from the sync tip into the work channel, until done or <see cref="ResumeRangeSyncFromHead"/> ends it.</summary>
    /// <remarks>
    /// A Gloas block carries only a bid, so consecutive Gloas blocks are buffered and their envelopes fetched with one
    /// ExecutionPayloadEnvelopesByRange request per run (gloas/p2p-interface.md); a run is written out at
    /// <see cref="RangeSync.DefaultBatchSize"/> blocks, before it would span more than <c>MAX_REQUEST_PAYLOADS</c> slots,
    /// at a pre-Gloas block, and at the end of the round.
    /// </remarks>
    internal async Task FeedRangeSyncRoundAsync(CancellationToken token)
    {
        // Before the tip, so a resume that moves the tip after this read also ends this round.
        CancellationToken restart = Volatile.Read(ref _rangeSyncRestart).Token;
        Tip tip = _syncTip;
        if (slotClock.CurrentSlot <= tip.Slot)
        {
            return;
        }

        using CancellationTokenSource round = CancellationTokenSource.CreateLinkedTokenSource(token, restart);
        token = round.Token;

        List<ForkedSignedBeaconBlock.OfGloas> gloasRun = [];
        await foreach (ForkedSignedBeaconBlock block in rangeSync.Run(tip.Root, tip.Slot, () => slotClock.CurrentSlot, token))
        {
            if (block is not ForkedSignedBeaconBlock.OfGloas gloas)
            {
                await WriteGloasRunAsync(gloasRun, token);
                await _work.Writer.WriteAsync(new RangeBlockItem(block), token);
                continue;
            }

            if (gloasRun.Count > 0 && gloas.Slot - gloasRun[0].Slot >= ExecutionPayloadEnvelopesProtocolBase.MaxRequestPayloads)
            {
                await WriteGloasRunAsync(gloasRun, token);
            }

            gloasRun.Add(gloas);
            if ((ulong)gloasRun.Count >= RangeSync.DefaultBatchSize)
            {
                await WriteGloasRunAsync(gloasRun, token);
            }
        }

        await WriteGloasRunAsync(gloasRun, token);
    }

    /// <summary>Writes each block of <paramref name="run"/> in order, each followed by its envelope when the chain carries that payload, then clears the run.</summary>
    /// <remarks>
    /// A block's payload is on the chain when the next block's bid builds on it (<c>bid.parent_block_hash</c> equals its
    /// <c>bid.block_hash</c>); the last block's envelope is written when a peer served it. A missing envelope a full child
    /// needs is recovered by root once that child is parked.
    /// </remarks>
    private async Task WriteGloasRunAsync(List<ForkedSignedBeaconBlock.OfGloas> run, CancellationToken token)
    {
        if (run.Count == 0)
        {
            return;
        }

        (SignedExecutionPayloadEnvelope Envelope, IBeaconSyncPeer Source)?[] envelopes = await FetchRunEnvelopesAsync(run, token);
        for (int i = 0; i < run.Count; i++)
        {
            await _work.Writer.WriteAsync(new RangeBlockItem(run[i]), token);
            if (envelopes[i] is { } served && (i == run.Count - 1 || IsPayloadOnChain(run[i], run[i + 1])))
            {
                await _work.Writer.WriteAsync(new RangeEnvelopeItem(served.Envelope, served.Source), token);
            }
        }

        run.Clear();
    }

    private static ExecutionPayloadBid BidOf(ForkedSignedBeaconBlock.OfGloas block) => block.Block.Message!.Body!.SignedExecutionPayloadBid!.Message!;

    private static bool IsPayloadOnChain(ForkedSignedBeaconBlock.OfGloas block, ForkedSignedBeaconBlock.OfGloas child) =>
        BidOf(child).ParentBlockHash == BidOf(block).BlockHash;

    /// <summary>
    /// Requests the envelopes of <paramref name="run"/> by range from up to <see cref="MaxBackfillPeersPerRequest"/> peers, until
    /// every block whose payload the chain carries has one; each envelope must name a block of the run and match its slot and bid.
    /// </summary>
    /// <returns>The first matching envelope for each block of <paramref name="run"/>, with the peer that served it, by index.</returns>
    private async Task<(SignedExecutionPayloadEnvelope Envelope, IBeaconSyncPeer Source)?[]> FetchRunEnvelopesAsync(List<ForkedSignedBeaconBlock.OfGloas> run, CancellationToken token)
    {
        (SignedExecutionPayloadEnvelope Envelope, IBeaconSyncPeer Source)?[] found = new (SignedExecutionPayloadEnvelope, IBeaconSyncPeer)?[run.Count];
        Dictionary<Hash256, int> indexByRoot = new(run.Count);
        for (int i = 0; i < run.Count; i++)
        {
            indexByRoot[run[i].ComputeMessageRoot()] = i;
        }

        ulong startSlot = run[0].Slot;
        ulong count = run[^1].Slot - startSlot + 1;
        // Status v2 earliest_available_slot (fulu/p2p-interface.md): prefer peers serving from the run start; with none, a peer serving part of the run still beats none.
        IReadOnlyList<IBeaconSyncPeer> reaching = peerPool.GetBestPeers(startSlot);
        IReadOnlyList<IBeaconSyncPeer> peers = [.. reaching.Where(peer => peer.EarliestAvailableSlot <= startSlot)];
        if (peers.Count == 0)
        {
            peers = [.. reaching.Where(peer => peer.EarliestAvailableSlot <= run[^1].Slot)];
        }
        for (int p = 0; p < peers.Count && p < MaxBackfillPeersPerRequest; p++)
        {
            IBeaconSyncPeer peer = peers[p];
            IReadOnlyList<SignedExecutionPayloadEnvelope> envelopes;
            try
            {
                envelopes = await peer.RequestExecutionPayloadEnvelopesByRangeAsync(startSlot, count, token);
            }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
            {
                peer.ReportFailure(PeerFailureClassifier.Classify(e), $"Execution-payload-envelopes-by-range from slot {startSlot} failed: {e.Message}");
                continue;
            }

            foreach (SignedExecutionPayloadEnvelope envelope in envelopes)
            {
                if (envelope.Message is not { BeaconBlockRoot: { } root } message
                    || !indexByRoot.TryGetValue(root, out int index)
                    || !MatchesBid(message, run[index]))
                {
                    peer.ReportFailure(PeerFailureReason.ProtocolViolation, $"Execution-payload-envelopes-by-range from slot {startSlot} returned an envelope for no block of the range");
                    continue;
                }

                found[index] ??= (envelope, peer);
            }

            if (HasEveryOnChainEnvelope(run, found))
            {
                break;
            }
        }

        return found;
    }

    private static bool MatchesBid(ExecutionPayloadEnvelope message, ForkedSignedBeaconBlock.OfGloas block)
    {
        ExecutionPayloadBid bid = BidOf(block);
        return message.Payload is { } payload
            && payload.SlotNumber == block.Slot
            && message.BuilderIndex == bid.BuilderIndex
            && payload.BlockHash == bid.BlockHash;
    }

    private static bool HasEveryOnChainEnvelope(List<ForkedSignedBeaconBlock.OfGloas> run, (SignedExecutionPayloadEnvelope, IBeaconSyncPeer)?[] found)
    {
        for (int i = 0; i < run.Count - 1; i++)
        {
            if (found[i] is null && IsPayloadOnChain(run[i], run[i + 1]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Dials discovered candidates (bounded concurrency) until the target peer count is reached, then idles.</summary>
    private async Task RunDiscoveryDialLoopAsync(CancellationToken token)
    {
        // A dropped peer becomes re-dialable after a cooldown - dialable mainnet peers are
        // scarce, so permanently blacklisting every drop starves the pool.
        if (peerManager is PeerManager manager)
        {
            manager.PeerDropped += peerId => _ = Task.Delay(TimeSpan.FromMinutes(2), token)
                .ContinueWith(_ => _dialedPeerIds.TryRemove(peerId, out byte _), token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }

        using SemaphoreSlim dialGate = new(ConcurrentDials);
        await foreach (BeaconPeerCandidate candidate in discovery!.DiscoverPeers(token))
        {
            // PeerManager owns the target band (and the ban list and dial gate behind it), so this
            // loop asks whether there is room rather than comparing PeerCount to config itself - the
            // orchestrator's own comparison here and PeerManager's admission check used to disagree.
            await peerManager!.WaitForAdmissionCapacityAsync(token);

            if (!_dialedPeerIds.TryAdd(candidate.PeerId, 0))
            {
                continue;
            }

            Metrics.BeaconChainDialAttempts++;
            await dialGate.WaitAsync(token);
            _ = DialCandidateAsync(candidate, dialGate, token);
        }
    }

    private async Task DialCandidateAsync(BeaconPeerCandidate candidate, SemaphoreSlim dialGate, CancellationToken token)
    {
        try
        {
            if (!await peerManager!.TryAddPeerAsync(candidate.Multiaddress, token, candidate.Enr))
            {
                // Allow a later re-dial when the peer shows up again.
                _dialedPeerIds.TryRemove(candidate.PeerId, out _);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _dialedPeerIds.TryRemove(candidate.PeerId, out _);
            if (_logger.IsDebug) _logger.Debug($"Dialing discovered beacon peer {candidate.Multiaddress} failed: {e.Message}");
        }
        finally
        {
            dialGate.Release();
        }
    }

    /// <summary>Logs sync progress at most once per second, and only when the sync tip's slot has moved, until the node follows head in sync.</summary>
    /// <remarks>Once following head with the execution layer in sync, the per-epoch status line is enough.</remarks>
    internal void LogSyncProgress(ulong slot)
    {
        _blocksSinceProgressLog++;
        long now = slotClock.UnixMilliseconds;
        ulong wallSlot = slotClock.CurrentSlot;
        ulong behind = wallSlot > slot ? wallSlot - slot : 0;
        if (slot <= _progressLogSlot || now - _progressLogMs < ProgressLogIntervalMs || (_elInSync && behind <= FollowingHeadSlackSlots) || !_logger.IsInfo)
        {
            return;
        }

        double seconds = (now - _progressLogMs) / 1000.0;
        ulong newPayloadMs = Metrics.BeaconChainNewPayloadMilliseconds;
        _logger.Info($"Beacon sync: slot {slot} (+{slot - _progressLogSlot} slots, {_blocksSinceProgressLog / seconds:F1} blocks/s, {_importMsSinceProgressLog / _blocksSinceProgressLog} ms/block of which newPayload {(long)(newPayloadMs - _newPayloadMsAtProgressLog) / _blocksSinceProgressLog} ms), {behind} behind wall slot {wallSlot}, finalized epoch {_lastHead?.Finalized.Epoch ?? 0}, peers {peerManager?.PeerCount ?? 0}/{config.TargetPeerCount}, EL {(_elInSync ? "in sync" : "syncing")}");
        _progressLogSlot = slot;
        _progressLogMs = now;
        _blocksSinceProgressLog = 0;
        _importMsSinceProgressLog = 0;
        _newPayloadMsAtProgressLog = newPayloadMs;
    }

    private sealed record ParkedEnvelope(SignedExecutionPayloadEnvelope Envelope, IBeaconSyncPeer? Source, ulong QueuedAtSlot);

    /// <summary>Envelopes held by the block root they name, first come: at most <paramref name="perBlock"/> for one root and <paramref name="total"/> in all.</summary>
    /// <remarks>An envelope already held is not held again, so a retry keeps the slot it was first queued at. Worker-only, not thread-safe.</remarks>
    private sealed class EnvelopeParking(int perBlock, int total)
    {
        private readonly Dictionary<Hash256, List<ParkedEnvelope>> _byBlock = [];
        private int _count;

        public bool Contains(Hash256 blockRoot) => _byBlock.ContainsKey(blockRoot);

        /// <returns>Whether the envelope is held.</returns>
        public bool Add(Hash256 blockRoot, SignedExecutionPayloadEnvelope envelope, IBeaconSyncPeer? source, ulong queuedAtSlot)
        {
            if (!_byBlock.TryGetValue(blockRoot, out List<ParkedEnvelope>? held))
            {
                if (_count >= total)
                {
                    return false;
                }

                _byBlock[blockRoot] = held = [];
            }

            foreach (ParkedEnvelope parked in held)
            {
                if (ReferenceEquals(parked.Envelope, envelope))
                {
                    return true;
                }
            }

            if (held.Count >= perBlock || _count >= total)
            {
                return false;
            }

            held.Add(new ParkedEnvelope(envelope, source, queuedAtSlot));
            _count++;
            return true;
        }

        /// <summary>Removes and returns the envelopes held for <paramref name="blockRoot"/> in arrival order.</summary>
        public List<ParkedEnvelope>? Take(Hash256 blockRoot)
        {
            if (!_byBlock.Remove(blockRoot, out List<ParkedEnvelope>? held))
            {
                return null;
            }

            _count -= held.Count;
            return held;
        }

        public void Remove(ParkedEnvelope parked)
        {
            Hash256 blockRoot = parked.Envelope.Message!.BeaconBlockRoot!;
            if (_byBlock.TryGetValue(blockRoot, out List<ParkedEnvelope>? held) && held.Remove(parked))
            {
                _count--;
                if (held.Count == 0)
                {
                    _byBlock.Remove(blockRoot);
                }
            }
        }

        public void RemoveWhere(Func<ParkedEnvelope, bool> predicate)
        {
            foreach (ParkedEnvelope parked in Snapshot())
            {
                if (predicate(parked))
                {
                    Remove(parked);
                }
            }
        }

        /// <summary>The envelopes held for <paramref name="blockRoot"/> in arrival order, still held.</summary>
        public List<ParkedEnvelope> HeldFor(Hash256 blockRoot) => _byBlock.TryGetValue(blockRoot, out List<ParkedEnvelope>? held) ? [.. held] : [];

        public List<ParkedEnvelope> Snapshot()
        {
            List<ParkedEnvelope> all = new(_count);
            foreach (List<ParkedEnvelope> held in _byBlock.Values)
            {
                all.AddRange(held);
            }

            return all;
        }
    }
}
