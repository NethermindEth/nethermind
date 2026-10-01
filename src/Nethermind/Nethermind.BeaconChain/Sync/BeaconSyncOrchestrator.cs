// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
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
/// feed) only write to a bounded channel; one worker loop consumes it and is the only caller of the
/// <see cref="IBlockImporter"/> (and through it the state transition and fork choice, neither of which is
/// thread-safe). Each import runs to completion on <see cref="ImportThread"/> before the next starts, as its
/// engine call blocks. A fork-choice head step - <c>engine_forkchoiceUpdated</c>,
/// finality handling, status refresh - runs on the worker after every drained import batch (or
/// every <see cref="HeadStepImportInterval"/> imports or <see cref="HeadStepInterval"/> while saturated) and on every slot tick.
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
    ExecutionPayloadEnvelopePool? envelopePool = null,
    HeadSnapshotHolder? headSnapshots = null,
    ColumnBackfill? columnBackfill = null)
{
    /// <summary>Maximum parent-chain depth fetched by root for a gossip block with an unknown parent.</summary>
    private const int MaxBackfillDepth = 32;

    /// <summary>Peers tried per by-root fetch before giving the block up.</summary>
    private const int MaxBackfillPeersPerRequest = 3;

    /// <summary>By-root backfills started per wall-clock slot for gossip blocks with an unknown parent.</summary>
    internal const int MaxBackfillsPerSlot = 4;

    /// <summary>Gossip blocks the backfill budget refused that are held for their parent at once.</summary>
    internal const int MaxHeldRefusedBackfills = 4 * MaxBackfillsPerSlot;

    /// <summary>The most distinct roots fetched by root at once for the ancestors of gossip blocks.</summary>
    internal const int MaxConcurrentAncestorFetches = 8;

    private const int MaxPendingGossipBlocks = 128;

    /// <summary>Bounds the range-synced blocks held for a deferred block, so gossip blocks held for a parent keep the rest of <see cref="MaxPendingGossipBlocks"/>.</summary>
    internal const int MaxRangeHeldBlocks = MaxPendingGossipBlocks / 2;

    /// <summary>The most held range blocks whose missing columns are fetched at once, so the blocks behind a deferred one do not wait for each other's fetch.</summary>
    internal const int MaxConcurrentHeldColumnFetches = 8;

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

    /// <summary>The most queued votes that wait for a slot tick the worker has not reached.</summary>
    internal const int MaxVotesAheadOfTick = VoteQueueCapacity / 4;

    /// <summary>The most gossip votes and slashings verified per worker pass, so a flood of them delays queued blocks by one batch at most.</summary>
    internal const int VotesPerPass = 64;

    /// <summary>Head-step cadence while the work queue never drains (deep range sync).</summary>
    private const int HeadStepImportInterval = 64;

    /// <summary>The longest the worker imports without a head step, so the execution layer's head follows fork choice while range sync imports behind the wall clock.</summary>
    private static readonly TimeSpan HeadStepInterval = TimeSpan.FromSeconds(1);

    /// <summary>Head distance (~2 epochs) below which gossip is started while range sync finishes the residual gap.</summary>
    private const ulong GossipStartDistanceSlots = 64;

    // At a slot tick the newest block is usually the previous slot's, and one missed proposal adds another.
    private const ulong FollowingHeadSlackSlots = 2;

    private const long ProgressLogIntervalMs = 1000;

    private readonly ILogger _logger = logManager.GetClassLogger<BeaconSyncOrchestrator>();

    /// <summary>Runs the importer's block and envelope imports, whose engine call blocks, off the thread pool.</summary>
    private readonly ImportThread _importThread = new();
    private readonly Channel<WorkItem> _work = Channel.CreateBounded<WorkItem>(
        new BoundedChannelOptions(WorkQueueCapacity) { SingleReader = true });

    // Each queued vote costs a BLS verify and a forged one is never penalized, so votes wait apart: a flood of them must not fill _work and drop gossip blocks.
    private readonly Channel<QueuedVote> _votes = Channel.CreateBounded<QueuedVote>(
        new BoundedChannelOptions(VoteQueueCapacity) { SingleReader = true });

    /// <summary>1 while a <see cref="VoteWakeItem"/> may be queued in the work channel, so a vote flood adds at most one item there.</summary>
    private int _voteWakeQueued;

    private ulong _publishedHeadSlot;

    /// <summary>The newest slot tick the worker has read; a vote queued after a newer tick waits for that tick.</summary>
    private ulong _reachedSlotTick;

    /// <summary>The newest slot tick fork choice has been ticked to; touched by the worker only.</summary>
    private ulong _appliedSlotTick;

    /// <summary>Votes queued and not yet read that were stamped with a tick the worker had not reached.</summary>
    private int _votesAheadOfTick;

    /// <summary>The newest slot tick queued, written before the tick is; a queued tick older than it is skipped.</summary>
    private ulong _newestSlotTick;

    private ulong _statusLogEpoch;

    /// <summary>Gossip blocks waiting for their parent, keyed by the unknown parent root.</summary>
    private readonly Dictionary<Hash256, List<ForkedSignedBeaconBlock>> _pendingByParent = [];

    /// <summary>Blocks that returned <see cref="BlockImportResult.DataUnavailable"/>, <see cref="BlockImportResult.EngineUnavailable"/>, <see cref="BlockImportResult.ParentPayloadUnverified"/> or <see cref="BlockImportResult.FutureSlot"/>, keyed by block root, awaiting a retry.</summary>
    private readonly Dictionary<Hash256, PendingRetry> _pendingRetry = [];

    /// <summary>Blocks fetched by root that wait in <see cref="_pendingByParent"/> behind a fetched ancestor, so they import as fetched; bounded like that queue.</summary>
    private readonly LruCache<Hash256, HeldFetchedBlock> _heldFetched = new(MaxPendingGossipBlocks, nameof(_heldFetched));

    /// <summary>The custodians asked for the missing columns of each block in <see cref="_pendingRetry"/>, kept across slots so every custodian is reached.</summary>
    private readonly Dictionary<Hash256, RangeSync.ColumnFetchRotation> _columnFetchRotations = [];

    /// <summary>The roots whose by-root column fetch is running, at most one each; the fetch runs off the worker and ends with a <see cref="ColumnFetchEndedItem"/>.</summary>
    private readonly HashSet<Hash256> _columnFetchesInFlight = [];

    /// <summary>Deferred blocks whose fetch was running when a custodian was admitted; the fetch asks again when it ends short, as that custodian may not be in its rotation.</summary>
    private readonly HashSet<Hash256> _fetchAgainOnEnd = [];

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
    private readonly LruCache<Hash256, RangeSync.ColumnFetchRotation> _envelopePeerSelections = new(RecentEnvelopeRequestCapacity, "beacon sync envelope peers");

    private readonly LruCache<Hash256, RangeSync.ColumnFetchRotation> _rangeEnvelopePeerSelections = new(RecentEnvelopeRequestCapacity, "beacon sync range envelope peers");

    /// <summary>Imported Gloas blocks whose bid commits blobs, by root, whose sampled columns are recovered by root until all are held.</summary>
    private readonly Dictionary<Hash256, ColumnRecovery> _columnRecovery = [];

    /// <summary>The checkpoint anchor's root, whose payload no import of this process recorded (specs/gloas/fork-choice.md get_forkchoice_store: <c>payloads={}</c>).</summary>
    private Hash256? _anchorRoot;

    /// <summary>The unknown parent roots whose backfill started in wall-clock slot <see cref="_backfillSlot"/>.</summary>
    private readonly HashSet<Hash256> _backfilledParents = [];
    private int _backfillsThisSlot;
    private ulong _backfillSlot;

    /// <summary>The gossip blocks held in <see cref="_pendingByParent"/> after the backfill budget refused them, oldest first.</summary>
    private readonly Queue<ForkedSignedBeaconBlock> _heldForBackfill = new();

    /// <summary>The ancestor roots whose by-root fetch runs off the worker, with the chains waiting on each, gossip block first; a fetch ends with an <see cref="AncestorFetchedItem"/>.</summary>
    private readonly Dictionary<Hash256, List<(List<ForkedSignedBeaconBlock> Chain, List<IBeaconSyncPeer?> Sources)>> _ancestorFetches = [];


    private IBlockImporter? _importer;

    /// <summary>Answers the PTC of a head root and slot for the gossip router; tests script it, otherwise the <see cref="BlockImporter"/> reads its head state.</summary>
    internal Func<Hash256, ulong, ulong[]?>? PtcReader { get; set; }

    private volatile Tip _syncTip = new(Hash256.Zero, 0);

    /// <summary>Cancelled and replaced to restart range sync; see <see cref="ResumeRangeSyncFromHead"/>.</summary>
    private CancellationTokenSource _rangeSyncRestart = new();
    private ulong? _rangeSyncResumedAtSlot;

    /// <summary>Cancelled and replaced to end the feed's wait between rounds without ending a round in flight; see <see cref="WakeRangeSyncForAncestors"/>.</summary>
    private CancellationTokenSource _rangeSyncWake = new();
    private ulong? _ancestorWakeSlot;

    /// <summary>The newest range-synced block that waits in <see cref="_pendingRetry"/> or is held under it, so a round starts past it; written by the worker, read by the feed.</summary>
    private volatile HeldRange? _rangeHeld;

    /// <summary>The roots and suppliers of the range-synced blocks held on the chain of <see cref="_rangeHeld"/>; touched by the worker only.</summary>
    private readonly Dictionary<Hash256, IBeaconSyncPeer?> _rangeHeldRoots = [];

    /// <summary>The held range blocks whose by-root column fetch runs ahead of their turn to import, at most <see cref="MaxConcurrentHeldColumnFetches"/>.</summary>
    private readonly HashSet<Hash256> _heldColumnFetches = [];

    /// <summary>The held range blocks waiting for a place among <see cref="_heldColumnFetches"/>, oldest first; bounded by <see cref="MaxRangeHeldBlocks"/>.</summary>
    private readonly Queue<(Hash256 Root, BeaconBlock Message)> _heldColumnFetchQueue = new();

    /// <summary>Refused blocks whose columns a held fetch is already fetching, imported once that fetch ends complete; at most <see cref="MaxConcurrentHeldColumnFetches"/>.</summary>
    private readonly Dictionary<Hash256, ForkedSignedBeaconBlock> _refusedOnHeldFetch = [];

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

    /// <summary>When the last head step started, on the slot clock.</summary>
    private long _headStepMs;
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

    /// <summary>The range-synced chain held for the deferred block <paramref name="DeferredRoot"/>, up to <paramref name="Tip"/>.</summary>
    private sealed record HeldRange(Hash256 DeferredRoot, Tip Tip);

    internal abstract record WorkItem;
    internal sealed record RangeBlockItem(ForkedSignedBeaconBlock Block, IBeaconSyncPeer? Source = null, CancellationToken RoundToken = default) : WorkItem;
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

    /// <summary>The by-root fetch of the ancestor <paramref name="Root"/> ended; <paramref name="Block"/> is <c>null</c> when no peer returned it.</summary>
    internal sealed record AncestorFetchedItem(Hash256 Root, ForkedSignedBeaconBlock? Block, IBeaconSyncPeer? Source) : WorkItem;

    /// <summary><paramref name="Peer"/> was admitted; a deferred block missing a column it custodies is fetched from it now, not at the next slot tick.</summary>
    internal sealed record PeerAdmittedItem(IBeaconSyncPeer Peer) : WorkItem;

    /// <summary>Wakes the worker for queued gossip votes; carries no work of its own.</summary>
    private sealed record VoteWakeItem : WorkItem
    {
        public static readonly VoteWakeItem Instance = new();
    }

    /// <summary>A queued gossip vote; <paramref name="NewestSlotTick"/> is the newest slot tick queued before it.</summary>
    private readonly record struct QueuedVote(WorkItem Item, ulong NewestSlotTick, bool AheadOfTick);

    /// <summary>An execution payload envelope to import; <paramref name="Source"/> is the req/resp peer that served it, if any.</summary>
    internal abstract record EnvelopeItem(SignedExecutionPayloadEnvelope Envelope, IBeaconSyncPeer? Source) : WorkItem;
    internal sealed record GossipEnvelopeItem(SignedExecutionPayloadEnvelope Envelope) : EnvelopeItem(Envelope, null);
    internal sealed record RangeEnvelopeItem(SignedExecutionPayloadEnvelope Envelope, IBeaconSyncPeer Source) : EnvelopeItem(Envelope, Source);
    internal sealed record FetchedEnvelopeItem(SignedExecutionPayloadEnvelope Envelope, IBeaconSyncPeer? Source) : EnvelopeItem(Envelope, Source);

    /// <param name="Origin">How the block reached this node, so every retry imports it the same way.</param>
    /// <param name="ServedBy">The peer that served a block fetched by root, blamed if a retry finds it invalid.</param>
    /// <param name="AwaitsRegeneration">Whether the block waits for the next slot's regeneration budget rather than for data or a payload.</param>
    private readonly record struct PendingRetry(ForkedSignedBeaconBlock Block, ulong QueuedAtSlot, ImportOrigin Origin, IBeaconSyncPeer? ServedBy, bool AwaitsRegeneration);

    /// <summary>A block fetched by root and held behind a fetched ancestor, with the peer that served it.</summary>
    private sealed record HeldFetchedBlock(ForkedSignedBeaconBlock Block, IBeaconSyncPeer? ServedBy);

    /// <summary>How a block reached this node, which decides the regeneration budget its import is charged to.</summary>
    private enum ImportOrigin
    {
        Gossip,
        Range,
        ByRoot,
    }

    private sealed record ColumnRecovery(ExecutionPayloadBid Bid, ulong QueuedAtSlot, ulong LastAttemptSlot, RangeSync.ColumnFetchRotation Rotation);

    /// <summary>Whether gossip has started; settable by tests to keep it from starting.</summary>
    internal bool GossipStarted { get; set; }

    /// <summary>A work item or vote that holds the worker at least this long is logged at Debug with what it was.</summary>
    internal TimeSpan SlowWorkItemThreshold { get; set; } = TimeSpan.FromSeconds(1);

    internal byte[] CurrentGossipDigest => _currentDigest;

    internal (Hash256 Root, ulong Slot) SyncTip => (_syncTip.Root, _syncTip.Slot);

    internal ChannelWriter<WorkItem> WorkWriter => _work.Writer;

    /// <summary>Completes when a work item is queued, without taking it; for tests that drive the worker by hand.</summary>
    internal ValueTask<bool> WaitForWorkAsync(CancellationToken token) => _work.Reader.WaitToReadAsync(token);

    /// <summary>The work items queued and not yet taken; for tests.</summary>
    internal int QueuedWorkCount => _work.Reader.Count;

    /// <summary>The by-root column fetches running off the worker; for tests.</summary>
    internal int ColumnFetchesInFlight => _columnFetchesInFlight.Count;

    /// <summary>The by-root ancestor fetches running off the worker, bounded by <see cref="MaxConcurrentAncestorFetches"/>; for tests.</summary>
    internal int AncestorFetchesInFlight => _ancestorFetches.Count;

    /// <summary>The gossip blocks held for a parent, bounded by <see cref="MaxPendingGossipBlocks"/>; for tests.</summary>
    internal int PendingGossipBlockCount => _pendingCount;

    /// <summary>The blocks whose by-root column fetches are tracked, bounded by the retry set; for tests.</summary>
    internal int ColumnFetchRotationCount => _columnFetchRotations.Count;

    /// <summary>The blocks awaiting a data or engine retry, bounded by <see cref="MaxPendingRetryBlocks"/>; for tests.</summary>
    internal int PendingRetryBlockCount => _pendingRetry.Count;

    /// <summary>The slot of the newest range-synced block held for a deferred block, or <c>null</c> when none is; for tests.</summary>
    internal ulong? RangeHeldSlot => _rangeHeld?.Tip.Slot;

    /// <summary>The held blocks waiting for a place among the by-root column fetches, bounded by <see cref="MaxRangeHeldBlocks"/>; for tests.</summary>
    internal int HeldColumnFetchQueueCount => _heldColumnFetchQueue.Count;

    /// <summary>Runs the full sync flow from the given anchor until cancelled.</summary>
    /// <param name="afterEngineKick">Runs once the execution layer has been pointed at the anchor and before any block is imported, so slow start-up work does not delay that first call.</param>
    public async Task RunAsync(ForkedBeaconState anchorState, ForkedSignedBeaconBlock anchorBlock, Hash256 anchorRoot, CancellationToken token, Action? afterEngineKick = null)
    {
        using CancellationTokenSource timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task run = RunCoreAsync(anchorState, anchorBlock, anchorRoot, token, afterEngineKick);
        Task timer = RunHeadSlotDelayTimerAsync(timerCancellation.Token);
        try
        {
            await run;
        }
        finally
        {
            await timerCancellation.CancelAsync();
            try
            {
                await timer;
            }
            catch (OperationCanceledException) when (timerCancellation.IsCancellationRequested)
            {
                if (_logger.IsTrace) _logger.Trace("Beacon head delay timer stopped.");
            }
        }
    }

    private async Task RunCoreAsync(ForkedBeaconState anchorState, ForkedSignedBeaconBlock anchorBlock, Hash256 anchorRoot, CancellationToken token, Action? afterEngineKick)
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
        PayloadStatusV1? kick = await KickExecutionAsync(anchorRoot).WaitAsync(token);
        if (_logger.IsInfo) _logger.Info(kick is null ? "Engine kick failed; the next head step sends it again" : $"Engine kick returned {kick.Status}{(kick.Status == PayloadStatus.Syncing ? " - execution layer is syncing toward the anchor" : "")}");

        // A run stopped during the kick must not start the work that follows it.
        token.ThrowIfCancellationRequested();
        afterEngineKick?.Invoke();

        // Inbound peers connect and discovery fills while the replay runs; dials and range sync wait for the loops below.
        await StartComponentAsync(p2p.StartAsync, token);
        await StartComponentAsync(discovery.Start, token);

        await ReplayStoredBlocksAsync(token);

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

    /// <summary>Wait before a networking component whose start failed, e.g. on a port in use, is started again.</summary>
    internal TimeSpan ComponentStartRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Runs <paramref name="start"/> until it succeeds or <paramref name="token"/> is cancelled, so a failed bind does not leave the node without a driver.</summary>
    internal async Task StartComponentAsync(Func<CancellationToken, Task> start, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await start(token);
                return;
            }
            catch (Exception e) when (!token.IsCancellationRequested)
            {
                if (_logger.IsWarn) _logger.Warn($"Beacon networking startup failed: {CheckpointSync.DescribeCause(e)}; retrying in {ComponentStartRetryDelay.TotalSeconds:F0} s.");
                await Task.Delay(ComponentStartRetryDelay, token);
            }
        }
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
        _anchorRoot = anchorRoot;
        _anchorExecutionHash = anchorBlock switch
        {
            ForkedSignedBeaconBlock.OfFulu fulu => fulu.Block.Message!.Body!.ExecutionPayload!.BlockHash!,
            ForkedSignedBeaconBlock.OfGloas gloas => gloas.Block.Message!.Body!.SignedExecutionPayloadBid!.Message!.ParentBlockHash!,
            _ => throw new NotSupportedException($"Unhandled anchor block {anchorBlock.GetType().Name}"),
        };
        _syncTip = new Tip(anchorRoot, _anchorSlot);
        Volatile.Write(ref _publishedHeadSlot, _anchorSlot);
        RefreshHeadSlotDelay();
        _progressLogSlot = _anchorSlot;
        _progressLogMs = slotClock.UnixMilliseconds;
        _headStepMs = _progressLogMs;
        _newPayloadMsAtProgressLog = Metrics.BeaconChainNewPayloadMilliseconds;

        ulong epoch = slotClock.CurrentEpoch;
        _currentDigest = GossipTopics.CurrentDigest(spec, epoch);
        _nextRotation = GossipTopics.NextRotation(spec, epoch);

        importer.OnSlotTick(slotClock.CurrentSlot);
        statusHolder.EarliestAvailableSlotSource = EarliestAvailableSlot;
        StatusMessageV2 anchorStatus = new()
        {
            ForkDigest = _currentDigest,
            FinalizedRoot = anchorRoot,
            FinalizedEpoch = spec.GetEpoch(_anchorSlot),
            HeadRoot = anchorRoot,
            HeadSlot = _anchorSlot,
        };
        statusHolder.CurrentStatus = anchorStatus;
        if (headSnapshots is not null) headSnapshots.Current = new HeadSnapshot(anchorStatus, null, Hash256.Zero, false);
    }

    /// <summary>The earliest held block, raised by incomplete columns once all retention blocks are held (fulu/p2p-interface.md Status v2).</summary>
    private ulong EarliestAvailableSlot()
    {
        ulong blocksFrom = Math.Min(_anchorSlot, columnBackfill?.CompleteFrom ?? _anchorSlot);
        if (columnPool is null || slotClock.CurrentEpoch < spec.FuluForkEpoch)
        {
            return blocksFrom;
        }

        ulong serveFrom = DataAvailabilityBoundary.ComputeStartSlot(slotClock.CurrentEpoch, spec);
        if (blocksFrom > serveFrom)
        {
            return blocksFrom;
        }

        ulong columnsFrom = Math.Min(columnPool.EarliestCompletelyServableSlot, slotClock.CurrentSlot + 1);
        return columnsFrom <= serveFrom ? BlobSidecarFloor(blocksFrom) : columnsFrom;
    }

    /// <summary>Pre-Fulu blob sidecars are not retained, so full block coverage still requires a Fulu floor (fulu/p2p-interface.md).</summary>
    private ulong BlobSidecarFloor(ulong blocksFrom)
    {
        ulong epoch = slotClock.CurrentEpoch;
        ulong blobWindowStart = (epoch >= DataAvailabilityBoundary.MinEpochsForBlobSidecarsRequests ? epoch - DataAvailabilityBoundary.MinEpochsForBlobSidecarsRequests : 0) * spec.SlotsPerEpoch;
        ulong fuluSlot = spec.FuluForkEpoch * spec.SlotsPerEpoch;
        return blocksFrom <= blobWindowStart && blobWindowStart < fuluSlot ? fuluSlot : blocksFrom;
    }

    /// <summary>
    /// Re-imports the canonical blocks already persisted between the anchor and the wall clock, so
    /// a restart does not refetch them from the network.
    /// </summary>
    /// <remarks>
    /// Blocks are imported with signature verification off: they were fully verified before being
    /// persisted. Parent linkage is still checked so a stale index tail (e.g. entries past an
    /// unfinalized reorg point) stops the replay and leaves the rest to range sync. The anchor's and every stored Gloas block's
    /// verified envelope is imported right after its block, so its full children need no fetch; without one,
    /// a stored Gloas block that builds on its parent's full payload stands in for that parent's verified
    /// envelope (see <see cref="IBlockImporter.Import"/>).
    /// </remarks>
    internal async Task ReplayStoredBlocksAsync(CancellationToken token)
    {
        IBlockImporter importer = _importer!;
        Hash256 expectedParent = _syncTip.Root;
        int replayed = 0;
        ulong wallSlot = slotClock.CurrentSlot;
        // Fork choice starts with no payloads (gloas/fork-choice.md get_forkchoice_store), so the anchor's stored envelope is imported first.
        if (TryReadStoredEnvelope(expectedParent, out SignedExecutionPayloadEnvelope? anchorEnvelope))
        {
            await ImportEnvelopeAsync(anchorEnvelope, token);
        }

        for (ulong slot = _syncTip.Slot + 1; slot <= wallSlot; slot++)
        {
            token.ThrowIfCancellationRequested();
            if (!store.TryGetCanonicalRoot(slot, out Hash256? root) || !store.TryGetForkedBlock(root, out ForkedSignedBeaconBlock? block))
            {
                continue;
            }

            long startMs = Environment.TickCount64;
            if (block.ParentRoot != expectedParent || await _importThread.RunAsync(() => importer.Import(block, root, verifySignatures: false)) != BlockImportResult.Imported)
            {
                break;
            }

            Interlocked.Increment(ref Metrics.BlocksImportedCount);
            Metrics.BeaconChainLastBlockImportMs = Environment.TickCount64 - startMs;

            if (block is ForkedSignedBeaconBlock.OfGloas && TryReadStoredEnvelope(root, out SignedExecutionPayloadEnvelope? envelope))
            {
                await ImportEnvelopeAsync(envelope, token);
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

    /// <summary>Reads the stored envelope of <paramref name="root"/>; an unreadable record is logged and treated as absent, so it cannot stop the restart.</summary>
    private bool TryReadStoredEnvelope(Hash256 root, [NotNullWhen(true)] out SignedExecutionPayloadEnvelope? envelope)
    {
        try
        {
            return store.TryGetExecutionPayloadEnvelope(root, out envelope);
        }
        catch (InvalidDataException e)
        {
            if (_logger.IsWarn) _logger.Warn($"Stored execution payload envelope {root} is unreadable and is not replayed: {e.Message}");
            envelope = null;
            return false;
        }
    }

    /// <summary>The single consumer of the work and vote channels; completes when the work channel is completed or the token fires.</summary>
    internal async Task RunWorkerAsync(CancellationToken token)
    {
        ChannelReader<WorkItem> reader = _work.Reader;
        RoutePeerAdmissions();
        try
        {
            while (await reader.WaitToReadAsync(token))
            {
                await ProcessQueuedAsync(token);
            }
        }
        finally
        {
            peerPool.PeerAdmitted -= OnPeerAdmitted;
        }
    }

    /// <summary>Queues each admitted peer for the worker; an admission the full queue refuses is left to the slot tick.</summary>
    internal void RoutePeerAdmissions() => peerPool.PeerAdmitted += OnPeerAdmitted;

    private void OnPeerAdmitted(IBeaconSyncPeer peer) => _work.Writer.TryWrite(new PeerAdmittedItem(peer));

    /// <summary>
    /// Processes at most <see cref="VotesPerPass"/> queued gossip votes, then every queued work item, each slot tick after every vote
    /// queued before it, then runs a head step if any imported; one pass of <see cref="RunWorkerAsync"/>.
    /// </summary>
    internal async Task ProcessQueuedAsync(CancellationToken token)
    {
        // Votes first, so one queued before a slot tick is not checked against the next slot, but none queued after a tick the worker has not reached (gloas/fork-choice.md on_payload_attestation_message).
        for (int i = 0; i < VotesPerPass && TryReadVoteBefore(Volatile.Read(ref _reachedSlotTick) + 1, out QueuedVote vote); i++)
        {
            await ProcessVoteAsync(vote, token);
        }

        while (_work.Reader.TryRead(out WorkItem? item))
        {
            if (item is SlotTickItem tick)
            {
                while (TryReadVoteBefore(tick.Slot, out QueuedVote vote))
                {
                    await ProcessVoteAsync(vote, token);
                }

                Volatile.Write(ref _reachedSlotTick, Math.Max(_reachedSlotTick, tick.Slot));
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
            long startMs = Environment.TickCount64;
            await RunHeadStepAsync(token);
            if (IsSlowForDebug(startMs, out long elapsedMs)) _logger.Debug($"Import worker spent {elapsedMs} ms on the head step");
        }
    }

    // Reads the next vote only if it was queued before the tick of tickSlot; the single reader makes the peek and the read one step.
    private bool TryReadVoteBefore(ulong tickSlot, out QueuedVote vote)
    {
        if (!(_votes.Reader.TryPeek(out vote) && vote.NewestSlotTick < tickSlot && _votes.Reader.TryRead(out vote)))
        {
            return false;
        }

        if (vote.AheadOfTick)
        {
            Interlocked.Decrement(ref _votesAheadOfTick);
        }

        return true;
    }

    // A tick skipped in a backlog never ran on_tick, but a vote queued after it is checked against that slot (gloas/fork-choice.md on_payload_attestation_message).
    private async Task ProcessVoteAsync(QueuedVote vote, CancellationToken token)
    {
        if (vote.NewestSlotTick > _appliedSlotTick)
        {
            _appliedSlotTick = vote.NewestSlotTick;
            _importer!.OnSlotTick(vote.NewestSlotTick);
        }

        await ProcessItemAsync(vote.Item, token);
    }

    private async Task ProcessItemAsync(WorkItem item, CancellationToken token)
    {
        long startMs = Environment.TickCount64;
        switch (item)
        {
            case RangeBlockItem range:
                if (!range.RoundToken.IsCancellationRequested)
                {
                    await ImportRangeBlockAsync(range, token);
                }
                break;
            case GossipBlockItem gossip:
                await ProcessGossipBlockAsync(gossip.Block, token);
                break;
            case GossipAggregateItem { Aggregate: { Message: { Aggregate: { } vote } message } aggregate }:
                if (!gossipRouter.IsAggregateSeen(vote.Data!, vote.CommitteeBits!, vote.AggregationBits!, message.AggregatorIndex) && _importer!.OnGossipAggregate(aggregate))
                {
                    gossipRouter.MarkAggregateSeen(vote.Data!, vote.CommitteeBits!, vote.AggregationBits!, message.AggregatorIndex);
                }

                break;
            case GossipGloasAggregateItem { Aggregate: { Message: { Aggregate: { } vote } message } aggregate }:
                if (!gossipRouter.IsAggregateSeen(vote.Data!, vote.CommitteeBits!, vote.AggregationBits!, message.AggregatorIndex) && _importer!.OnGossipAggregate(aggregate))
                {
                    gossipRouter.MarkAggregateSeen(vote.Data!, vote.CommitteeBits!, vote.AggregationBits!, message.AggregatorIndex);
                }

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
                _appliedSlotTick = tick.Slot;
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
            case AncestorFetchedItem fetched:
                await OnAncestorFetchedAsync(fetched, token);
                break;
            case PeerAdmittedItem admitted:
                FetchDeferredColumnsFrom(admitted.Peer, token);
                break;
            case ColumnFetchEndedItem ended:
                _columnFetchesInFlight.Remove(ended.BlockRoot);
                if (_heldColumnFetches.Remove(ended.BlockRoot))
                {
                    StartHeldColumnFetches(token);
                }

                bool fetchAgain = _fetchAgainOnEnd.Remove(ended.BlockRoot);
                if (ended.Refused is not null)
                {
                    _refusedFetchRoot = null;
                }

                ForkedSignedBeaconBlock? refusedBlock = ended.Refused;
                if (_refusedOnHeldFetch.Remove(ended.BlockRoot, out ForkedSignedBeaconBlock? carried))
                {
                    refusedBlock ??= carried;
                }

                if (ended.Complete)
                {
                    _columnRecovery.Remove(ended.BlockRoot);
                    await RetryOnColumnsAsync(ended.BlockRoot, token);
                    if (refusedBlock is { } refused && !_pendingRetry.ContainsKey(ended.BlockRoot) && !_importer!.IsKnown(ended.BlockRoot))
                    {
                        await ImportBlockAsync(refused, token, retryingOnColumns: true);
                    }
                }
                else if (fetchAgain && _pendingRetry.TryGetValue(ended.BlockRoot, out PendingRetry waiting))
                {
                    AwaitColumns(waiting.Block, ended.BlockRoot, token);
                }

                break;
        }

        if (IsSlowForDebug(startMs, out long elapsedMs)) _logger.Debug($"Import worker spent {elapsedMs} ms on {DescribeWorkItem(item)}");
    }

    private bool IsSlowForDebug(long startMs, out long elapsedMs)
    {
        elapsedMs = Environment.TickCount64 - startMs;
        return elapsedMs >= SlowWorkItemThreshold.TotalMilliseconds && _logger.IsDebug;
    }

    private static string DescribeWorkItem(WorkItem item) => item switch
    {
        GossipAggregateItem { Aggregate.Message.Aggregate.Data: { Target: { } target } data } => $"a gossip aggregate for slot {data.Slot} with target epoch {target.Epoch} root {target.Root}",
        GossipGloasAggregateItem { Aggregate.Message.Aggregate.Data: { Target: { } target } data } => $"a gossip aggregate for slot {data.Slot} with target epoch {target.Epoch} root {target.Root}",
        RangeBlockItem range => $"the range block at slot {range.Block.Slot}",
        GossipBlockItem gossip => $"the gossip block at slot {gossip.Block.Slot}",
        SlotTickItem tick => $"the slot tick of slot {tick.Slot}",
        _ => item.GetType().Name,
    };

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

    /// <summary>Imports a range-synced block, and notes it when it waits for a retry or for a parent that does, so the next round does not fetch it again.</summary>
    private async Task ImportRangeBlockAsync(RangeBlockItem item, CancellationToken token)
    {
        ForkedSignedBeaconBlock block = item.Block;
        BlockImportResult result = await ImportBlockAsync(block, token, rangeItem: item);
        if (result is not (BlockImportResult.DataUnavailable or BlockImportResult.EngineUnavailable or BlockImportResult.ParentPayloadUnverified or BlockImportResult.FutureSlot or BlockImportResult.UnknownParent))
        {
            return;
        }

        Hash256 root = block.ComputeMessageRoot();
        HeldRange? held = _rangeHeld;
        Hash256 deferredRoot;
        if (result == BlockImportResult.UnknownParent)
        {
            // A block a restarted round delivers again is held once, but still extends the chain the round rebuilds.
            bool alreadyHeld = _pendingByParent.TryGetValue(block.ParentRoot, out List<ForkedSignedBeaconBlock>? siblings) && siblings.Any(sibling => sibling.ComputeMessageRoot() == root);

            // A child links to the newest held block, or to a deferred block, whose chain it then starts.
            if (held is not null && block.ParentRoot == held.Tip.Root)
            {
                deferredRoot = held.DeferredRoot;
            }
            else if (_pendingRetry.ContainsKey(block.ParentRoot))
            {
                deferredRoot = block.ParentRoot;
            }
            else
            {
                return;
            }

            if (!alreadyHeld && (_pendingCount >= MaxRangeHeldBlocks || !QueuePendingGossipBlock(block)))
            {
                return;
            }
        }
        else if (_pendingRetry.ContainsKey(root))
        {
            deferredRoot = root;
        }
        else
        {
            return;
        }

        if (held is null || block.Slot > held.Tip.Slot)
        {
            if (held?.DeferredRoot != deferredRoot)
            {
                ClearRangeHeldRoots();
            }

            _rangeHeld = new HeldRange(deferredRoot, new Tip(root, block.Slot));
        }

        // A block waiting on another chain's deferred block must not take this chain over.
        if (_rangeHeld?.DeferredRoot == deferredRoot && _rangeHeldRoots.TryAdd(root, item.Source) && result == BlockImportResult.UnknownParent)
        {
            QueueHeldColumnFetch(block, root, token);
        }
    }

    /// <summary>Has the columns of a held blob block fetched by root now, not when its parent imports, so the blocks behind a deferred block do not fetch one after another.</summary>
    private void QueueHeldColumnFetch(ForkedSignedBeaconBlock block, Hash256 root, CancellationToken token)
    {
        if (block is ForkedSignedBeaconBlock.OfFulu { Block.Message: { Body.BlobKzgCommitments.Length: > 0 } message })
        {
            _heldColumnFetchQueue.Enqueue((root, message));
            StartHeldColumnFetches(token);
        }
    }

    private void StartHeldColumnFetches(CancellationToken token)
    {
        while (_heldColumnFetches.Count < MaxConcurrentHeldColumnFetches && _heldColumnFetchQueue.TryDequeue(out (Hash256 Root, BeaconBlock Message) next))
        {
            Hash256 root = next.Root;
            BeaconBlock message = next.Message;
            // A block that left the held chain meanwhile needs nothing ahead of its turn.
            if (_rangeHeldRoots.ContainsKey(root) && StartColumnFetch(root, fetchToken => rangeSync.FetchColumnsByRootAsync(root, message, fetchToken), token))
            {
                _heldColumnFetches.Add(root);
            }
        }
    }

    /// <summary>Starts the by-root column fetch of each deferred block that misses a column <paramref name="peer"/> custodies.</summary>
    private void FetchDeferredColumnsFrom(IBeaconSyncPeer peer, CancellationToken token)
    {
        if (_pendingRetry.Count == 0 || columnPool is null || _custody.Current is not { } custody)
        {
            return;
        }

        PeerColumnCustody peerCustody = peer.Custody;
        foreach ((Hash256 root, PendingRetry retry) in _pendingRetry.ToArray())
        {
            if (custody.SampledColumns.Any(column => peerCustody.Custodies(column) && !columnPool.TryGet(root, column, out _)))
            {
                if (_columnFetchesInFlight.Contains(root))
                {
                    _fetchAgainOnEnd.Add(root);
                }
                else
                {
                    AwaitColumns(retry.Block, root, token);
                }
            }
        }
    }

    /// <summary>
    /// Imports one block and, on success, drains any gossip blocks that were waiting for it. The
    /// single choke point for all four callers of <see cref="IBlockImporter.Import"/> that can import, so this is
    /// also where a <see cref="BlockImportResult.DataUnavailable"/>, <see cref="BlockImportResult.ParentPayloadUnverified"/>,
    /// <see cref="BlockImportResult.FutureSlot"/> or <see cref="BlockImportResult.EngineUnavailable"/> result is remembered for a later retry -
    /// wiring it in at only one call site would leave the other three silently dropping it.
    /// </summary>
    /// <param name="retryingOnColumns">Whether this import was woken by the columns it waited for, so a repeat deferral waits for the slot tick instead of watching and fetching again.</param>
    /// <param name="servedBy">The peer that served a block fetched by root; <c>null</c> otherwise.</param>
    /// <param name="otherCopyPending">Whether another signed copy of the block's message still waits to import, so this copy's refusal leaves the held children to it.</param>
    internal async Task<BlockImportResult> ImportBlockAsync(ForkedSignedBeaconBlock block, CancellationToken token, bool retryingOnColumns = false, RangeBlockItem? rangeItem = null, bool fetchedByRoot = false, IBeaconSyncPeer? servedBy = null, bool otherCopyPending = false)
    {
        Hash256 root = block.ComputeMessageRoot();
        long startMs = Environment.TickCount64;
        // Provenance follows the exact signed block: a copy from gossip under the same message root may carry another signature.
        bool isQueued = _pendingRetry.TryGetValue(root, out PendingRetry queued) && IsSameSignedBlock(queued.Block, block);
        bool otherCopyQueued = otherCopyPending || (!isQueued && _pendingRetry.ContainsKey(root));
        HeldFetchedBlock? heldFetched = _heldFetched.TryGet(root, out HeldFetchedBlock? held) && IsSameSignedBlock(held.Block, block) ? held : null;
        if (heldFetched is not null)
        {
            _heldFetched.Delete(root);
        }

        ImportOrigin origin = fetchedByRoot || heldFetched is not null ? ImportOrigin.ByRoot
            : rangeItem is not null || _rangeHeldRoots.ContainsKey(root) ? ImportOrigin.Range
            : isQueued ? queued.Origin
            : ImportOrigin.Gossip;
        servedBy ??= isQueued ? queued.ServedBy : heldFetched?.ServedBy;
        (BlockImportResult result, ImportRefusal refusal) = await _importThread.RunAsync(() =>
        {
            BlockImportResult imported = origin == ImportOrigin.Gossip
                ? _importer!.Import(block, root, verifySignatures: true)
                : _importer!.ImportRequested(block, root, fetchedByRoot: origin == ImportOrigin.ByRoot);
            return (imported, _importer!.LastRefusal);
        });
        // Only a spent budget waits for the next slot; a missing key or a state that cannot be regenerated stays refused.
        bool regenerationDeferred = origin == ImportOrigin.ByRoot && result == BlockImportResult.UnknownParent && refusal == ImportRefusal.RegenerationBudget;

        // These results come after the importer verified the proposer signature.
        if (result is BlockImportResult.Imported or BlockImportResult.EngineUnavailable or BlockImportResult.ParentPayloadUnverified or BlockImportResult.FutureSlot)
        {
            gossipRouter.MarkProposalSeen(block.Slot, block.ProposerIndex);
        }

        if (result == BlockImportResult.Imported)
        {
            Interlocked.Increment(ref Metrics.BlocksImportedCount);
            Metrics.BeaconChainLastBlockImportMs = Environment.TickCount64 - startMs;
            _importMsSinceProgressLog += Metrics.BeaconChainLastBlockImportMs;
            _pendingRetry.Remove(root);
            _rangeHeldRoots.Remove(root);
            if (TrackColumnRecovery(root, block) is { } recovery)
            {
                RecoverColumns(root, recovery, slotClock.CurrentSlot, token);
            }

            await OnImportedAsync(root, block.Slot, token);
            // After the held children imported or deferred in turn, so a child that waits takes the chain over first.
            ReleaseRangeHeld(root);
        }
        else if (regenerationDeferred || result is BlockImportResult.DataUnavailable or BlockImportResult.EngineUnavailable or BlockImportResult.ParentPayloadUnverified or BlockImportResult.FutureSlot)
        {
            if (!QueuePendingRetry(root, block, origin, servedBy, regenerationDeferred))
            {
                ReleaseImporterDeferral(root);
                DropPendingChildren(root, "the retry set is full");
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
            // Only the queued signed block itself leaves the retry set; an invalid copy under its root does not.
            bool wasRetried = isQueued && _pendingRetry.Remove(root);
            // The block has the root this node asked for, so the peer served an invalid block, unless only this node's own
            // fork-choice admission refused it (fork-choice.md on_block), which says nothing of the block's data.
            if (result == BlockImportResult.Invalid && origin == ImportOrigin.ByRoot && refusal != ImportRefusal.LocalAdmission)
            {
                servedBy?.ReportFailure(PeerFailureReason.ProtocolViolation, $"Blocks-by-root for {root} returned an invalid block");
            }

            if (result == BlockImportResult.Invalid && (rangeItem is not null || _rangeHeld?.DeferredRoot == root || _rangeHeldRoots.ContainsKey(root)))
            {
                // fork-choice.md on_block: rejected range blocks end the round and only their supplier is blamed.
                IBeaconSyncPeer? source = _rangeHeldRoots.GetValueOrDefault(root) ?? rangeItem?.Source;
                source?.ReportFailure(PeerFailureReason.ProtocolViolation, $"Invalid range block at slot {block.Slot}");
                _rangeHeld = null;
                ClearRangeHeldRoots();
                EndRangeSyncRound();
            }
            else if (wasRetried)
            {
                ReleaseRangeHeld(root);
            }

            // The children wait on the queued copy, which an invalid copy with another signature says nothing about.
            if (!otherCopyQueued && (result == BlockImportResult.Invalid || (wasRetried && result == BlockImportResult.UnknownParent)))
            {
                DropPendingChildren(root, result == BlockImportResult.Invalid ? "it is invalid" : "its parent is no longer known");
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

        // A held block's fetch ahead of its turn may still run: its end retries this block, so a second fetch would only duplicate it.
        bool heldFetchRunning = _heldColumnFetches.Contains(root);
        RangeSync.ColumnFetchRotation? rotation;
        if (refused)
        {
            if (heldFetchRunning)
            {
                _refusedOnHeldFetch[root] = block;
                return;
            }

            if (_refusedFetchRoot is not null)
            {
                return;
            }

            rotation = new RangeSync.ColumnFetchRotation(slotClock);
        }
        else
        {
            WatchColumns(root, gloas: false);
            if (heldFetchRunning)
            {
                _fetchAgainOnEnd.Add(root);
                return;
            }

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
    private bool QueuePendingRetry(Hash256 root, ForkedSignedBeaconBlock block, ImportOrigin origin, IBeaconSyncPeer? servedBy, bool awaitsRegeneration)
    {
        if (_pendingRetry.TryGetValue(root, out PendingRetry queued))
        {
            if (IsSameSignedBlock(queued.Block, block))
            {
                _pendingRetry[root] = queued = queued with { AwaitsRegeneration = awaitsRegeneration };
            }

            // A gossip block this node later fetched keeps its queue slot and age, and retries as requested from then on; only
            // the same signed block, so a copy with another signature never takes the fetched block's supplier.
            if (origin != ImportOrigin.Gossip && queued.Origin == ImportOrigin.Gossip && IsSameSignedBlock(queued.Block, block))
            {
                _pendingRetry[root] = queued with { Origin = origin, ServedBy = servedBy };
            }

            return true;
        }

        if (_pendingRetry.Count >= MaxPendingRetryBlocks)
        {
            return false;
        }

        _pendingRetry[root] = new PendingRetry(block, slotClock.CurrentSlot, origin, servedBy, awaitsRegeneration);
        // A held block that waits once the blocks before it imported holds the rest of the chain, as fulu/fork-choice.md is_data_available lets none of it import before it.
        if (_rangeHeld is { } held && (held.DeferredRoot == block.ParentRoot || _rangeHeldRoots.ContainsKey(root)))
        {
            _rangeHeld = held with { DeferredRoot = root };
        }

        return true;
    }

    /// <summary>Has the importer forget the deferral it kept for <paramref name="root"/>, a block the orchestrator dropped, instead of holding it until finality.</summary>
    private void ReleaseImporterDeferral(Hash256 root) => (_importer as BlockImporter)?.Release(root);

    /// <summary>Ends the held range chain of the deferred block <paramref name="root"/> once it leaves the retry set, so a round starts from the sync tip again.</summary>
    private void ReleaseRangeHeld(Hash256 root)
    {
        if (_rangeHeld?.DeferredRoot == root)
        {
            _rangeHeld = null;
            ClearRangeHeldRoots();
        }
    }

    /// <summary>Ends the range-sync round in flight and drops its queued blocks, so the next round starts from the sync tip.</summary>
    private void EndRangeSyncRound() => _ = Interlocked.Exchange(ref _rangeSyncRestart, new CancellationTokenSource()).CancelAsync();

    /// <summary>Moves the held tip back to the deferred block its chain waits on once the tip is dropped, so the next round fetches the dropped blocks again instead of starting past them.</summary>
    private void LowerRangeHeldTip()
    {
        if (_rangeHeld is not { } held)
        {
            return;
        }

        if (_pendingRetry.TryGetValue(held.DeferredRoot, out PendingRetry deferred))
        {
            _rangeHeld = held with { Tip = new Tip(held.DeferredRoot, deferred.Block.Slot) };
        }
        else
        {
            _rangeHeld = null;
            ClearRangeHeldRoots();
        }
    }

    private void ClearRangeHeldRoots()
    {
        _rangeHeldRoots.Clear();
        _heldColumnFetchQueue.Clear();
    }

    /// <summary>Keeps in the fetch queue only the blocks still held, so blocks dropped and fetched again cannot pile up behind fetches that wait on a slow peer.</summary>
    private void DropHeldColumnFetchesOfDroppedBlocks()
    {
        for (int waiting = _heldColumnFetchQueue.Count; waiting > 0; waiting--)
        {
            (Hash256 Root, BeaconBlock Message) next = _heldColumnFetchQueue.Dequeue();
            if (_rangeHeldRoots.ContainsKey(next.Root))
            {
                _heldColumnFetchQueue.Enqueue(next);
            }
        }
    }

    /// <summary>Forgets the blocks held for <paramref name="parentRoot"/>, and theirs in turn, once that parent can no longer import.</summary>
    /// <remarks>Otherwise they keep their share of <see cref="MaxPendingGossipBlocks"/> for the process lifetime. The drop is logged once and counted.</remarks>
    /// <param name="cause">Why <paramref name="parentRoot"/> cannot import, for the log line.</param>
    private void DropPendingChildren(Hash256 parentRoot, string cause)
    {
        bool tipDropped = _rangeHeld?.Tip.Root == parentRoot;
        if (!_pendingByParent.ContainsKey(parentRoot))
        {
            if (tipDropped)
            {
                LowerRangeHeldTip();
            }

            return;
        }

        int count = 0;
        Stack<Hash256> dropped = new();
        dropped.Push(parentRoot);
        while (dropped.TryPop(out Hash256? root))
        {
            if (!_pendingByParent.Remove(root, out List<ForkedSignedBeaconBlock>? children))
            {
                continue;
            }

            _pendingCount -= children.Count;
            count += children.Count;
            foreach (ForkedSignedBeaconBlock child in children)
            {
                Hash256 childRoot = child.ComputeMessageRoot();
                _heldForPayload.Remove(childRoot);
                _rangeHeldRoots.Remove(childRoot);
                tipDropped |= _rangeHeld?.Tip.Root == childRoot;
                ReleaseImporterDeferral(childRoot);
                dropped.Push(childRoot);
            }
        }

        if (tipDropped)
        {
            LowerRangeHeldTip();
        }

        if (count > 0)
        {
            DropHeldColumnFetchesOfDroppedBlocks();
            Metrics.BeaconChainHeldBlocksDropped += (ulong)count;
            if (_logger.IsInfo) _logger.Info($"Dropped the {count} held block{(count == 1 ? "" : "s")} behind {parentRoot}: {cause}");
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
                ReleaseImporterDeferral(root);
                ReleaseRangeHeld(root);
                _columnFetchRotations.Remove(root);
                ReleaseColumnWatch(root);
                DropPendingChildren(root, retry.Block.Slot <= finalizedSlot ? "it fell behind finality" : "its retry expired");
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
        bool Expired(ParkedEnvelope parked) => ((parked.Envelope.Message!.Payload?.SlotNumber ?? 0) <= finalizedSlot
            && !NeedsParentPayload(parked.Envelope.Message.BeaconBlockRoot!)) || IsRetryExpired(parked.QueuedAtSlot, currentSlot);
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
            if (!_columnRecovery.ContainsKey(root) && store.TryGetForkedBlock(root, out ForkedSignedBeaconBlock? block))
            {
                TrackColumnRecovery(root, block);
            }

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
    /// commits blobs, until every one is held, finality makes its payload unnecessary or it has waited past <see cref="MaxPendingRetryAgeEpochs"/>.
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
            if ((recovery.Bid.Slot <= finalizedSlot && !NeedsParentPayload(root)) || IsRetryExpired(recovery.QueuedAtSlot, currentSlot))
            {
                _columnRecovery.Remove(root);
                continue;
            }

            RecoverColumns(root, recovery, currentSlot, token);
        }
    }

    /// <summary>Whether <paramref name="root"/> is the anchor or the parent of a held or retried Gloas block, whose payload must verify before a FULL child imports (specs/gloas/fork-choice.md on_block).</summary>
    private bool NeedsParentPayload(Hash256 root)
    {
        if (root == _anchorRoot) return true;
        foreach (PendingRetry retry in _pendingRetry.Values)
        {
            if (retry.Block is ForkedSignedBeaconBlock.OfGloas && retry.Block.ParentRoot == root) return true;
        }

        if (_pendingByParent.TryGetValue(root, out List<ForkedSignedBeaconBlock>? children))
        {
            foreach (ForkedSignedBeaconBlock child in children)
            {
                if (_heldForPayload.Contains(child.ComputeMessageRoot())) return true;
            }
        }

        return false;
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
            result = await _importThread.RunAsync(() => _importer!.ImportEnvelope(envelope));
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
        if (!_envelopePeerSelections.TryGet(parentRoot, out RangeSync.ColumnFetchRotation? rotation))
        {
            rotation = new RangeSync.ColumnFetchRotation(slotClock);
            _envelopePeerSelections.Set(parentRoot, rotation);
        }
        IReadOnlyList<IBeaconSyncPeer> peers = rotation.Take(peerPool.GetBestPeers(0), MaxBackfillPeersPerRequest);
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
            Hash256[] childRoots = [.. children.Select(static child => child.ComputeMessageRoot())];
            for (int i = 0; i < children.Count; i++)
            {
                ForkedSignedBeaconBlock child = children[i];
                if (_heldForPayload.Count > 0)
                {
                    _heldForPayload.Remove(childRoots[i]);
                }

                // A forged copy held beside the genuine block must not drop the children that wait on the genuine one.
                bool otherCopyPending = false;
                for (int j = i + 1; j < children.Count && !otherCopyPending; j++)
                {
                    otherCopyPending = childRoots[j] == childRoots[i] && !IsSameSignedBlock(children[j], child);
                }

                await ImportBlockAsync(child, token, otherCopyPending: otherCopyPending);
            }
        }

        // A busy worker skips stale slot ticks, so the head step runs here too: the engine API wants forkchoiceUpdated after each head change.
        if (++_importsSinceHeadStep >= HeadStepImportInterval || slotClock.UnixMilliseconds - _headStepMs >= (long)HeadStepInterval.TotalMilliseconds)
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
            await HoldForWaitingParentAsync(block);
            return;
        }

        if (!importer.IsKnown(block.ParentRoot))
        {
            SignalChainAhead(block.Slot, $"gossip block at slot {block.Slot} has an unknown parent {block.ParentRoot}");
            // While far behind, range sync will deliver the parent chain anyway - just hold the
            // block; in steady state fetch the missing ancestors by root.
            if (_syncTip.Slot + MaxBackfillDepth < slotClock.CurrentSlot)
            {
                QueuePendingGossipBlock(block);
            }
            else if (_ancestorFetches.Count < MaxConcurrentAncestorFetches && TryTakeBackfill(block))
            {
                await AdvanceBackfillAsync([block], [null], token);
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Holding gossip block at slot {block.Slot} for unknown parent {block.ParentRoot}: {(_ancestorFetches.Count >= MaxConcurrentAncestorFetches ? "ancestor fetches at their bound" : _backfilledParents.Contains(block.ParentRoot) ? "that parent is being fetched or waits for a retry" : "backfill budget for this slot spent")}");
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
        // A Gloas block is retried for its parent's payload unless it waits for a regeneration.
        (_pendingRetry.TryGetValue(blockRoot, out PendingRetry parked) && parked.Block is ForkedSignedBeaconBlock.OfGloas && !parked.AwaitsRegeneration)
        || _heldForPayload.Contains(blockRoot);

    /// <summary>Holds <paramref name="block"/> until its parent, found by <see cref="IsWaitingForPayload"/>, imports.</summary>
    /// <remarks>
    /// The block is held only once the importer defers it too, which it does only after its proposer and proposer signature
    /// check out, and its (slot, proposer) is then marked seen: the queue is bounded, so unsigned or repeated proposals must
    /// not be able to crowd out the real block. The seen gate covers fetched ancestors too, or one equivocating proposer
    /// could fill the queue through forged children naming each of its blocks; such an ancestor imports once range sync or
    /// a later by-root fetch delivers it after its parent. A block the full queue cannot take is not marked seen.
    /// </remarks>
    /// <param name="fetchedByRoot">Whether this node fetched the block, so the importer does not charge it to the gossip regeneration budget.</param>
    /// <returns>Whether the block is held.</returns>
    private async Task<bool> HoldForWaitingParentAsync(ForkedSignedBeaconBlock block, bool fetchedByRoot = false)
    {
        Hash256 root = block.ComputeMessageRoot();
        if (_pendingCount >= MaxPendingGossipBlocks
            || gossipRouter.IsProposalSeen(block.Slot, block.ProposerIndex)
            || await _importThread.RunAsync(() => fetchedByRoot ? _importer!.ImportRequested(block, root, fetchedByRoot: true) : _importer!.Import(block, root, verifySignatures: true)) != BlockImportResult.ParentPayloadUnverified)
        {
            return false;
        }

        QueuePendingGossipBlock(block);
        gossipRouter.MarkProposalSeen(block.Slot, block.ProposerIndex);
        _heldForPayload.Add(root);
        return true;
    }

    /// <summary>Holds <paramref name="chain"/> from index <paramref name="from"/> down to its gossip block at index 0, stopping at the first block not held.</summary>
    private async Task HoldChainForWaitingParentAsync(List<ForkedSignedBeaconBlock> chain, int from)
    {
        for (int i = from; i >= 0; i--)
        {
            if (!await HoldForWaitingParentAsync(chain[i], fetchedByRoot: i > 0))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Walks the unknown ancestors of the gossip block <paramref name="chain"/>[0] by root, one fetch at a time and to a bounded
    /// depth, then imports the chain oldest-first once the parent of its last block is known.
    /// </summary>
    /// <remarks>
    /// A fetch can wait out several peers' timeouts, so it runs off the worker and the walk resumes on its
    /// <see cref="AncestorFetchedItem"/>; head imports and slot ticks do not wait behind it.
    /// </remarks>
    /// <param name="sources">The peer that served each block of <paramref name="chain"/>; <c>null</c> for the gossip block.</param>
    private async Task AdvanceBackfillAsync(List<ForkedSignedBeaconBlock> chain, List<IBeaconSyncPeer?> sources, CancellationToken token)
    {
        ForkedSignedBeaconBlock block = chain[0];
        Hash256 parent = chain[^1].ParentRoot;
        if (!_importer!.IsKnown(parent))
        {
            if (IsWaitingForPayload(parent))
            {
                await HoldChainForWaitingParentAsync(chain, chain.Count - 1);
            }
            else if (chain.Count > MaxBackfillDepth)
            {
                if (_logger.IsDebug) _logger.Debug($"Giving up on gossip block at slot {block.Slot}: ancestor chain exceeds {MaxBackfillDepth} unknown blocks");
            }
            else
            {
                StartAncestorFetch(parent, chain, sources, token);
                return;
            }

            EndBackfill(block);
            return;
        }

        for (int i = chain.Count - 1; i >= 0; i--)
        {
            BlockImportResult result = await ImportBlockAsync(chain[i], token, fetchedByRoot: i > 0, servedBy: sources[i]);
            if (result == BlockImportResult.ParentPayloadUnverified && i > 0 && _pendingRetry.ContainsKey(chain[i].ComputeMessageRoot()))
            {
                await HoldChainForWaitingParentAsync(chain, i - 1);
                break;
            }

            if (i > 0 && result == BlockImportResult.UnknownParent && _pendingRetry.ContainsKey(chain[i].ComputeMessageRoot()))
            {
                // The ancestor waits for the next slot's regeneration budget; its descendants import after it, the fetched ones as
                // fetched, held under the evicting cap a refused backfill has, so no fetched chain can fill the shared queue. Only the
                // descendants nearest the ancestor are held: the cap evicts the oldest held first, which would strand the rest.
                for (int j = i - 1; j >= Math.Max(0, i - MaxHeldRefusedBackfills); j--)
                {
                    HoldRefusedBackfill(chain[j]);
                    if (j > 0)
                    {
                        _heldFetched.Set(chain[j].ComputeMessageRoot(), new HeldFetchedBlock(chain[j], sources[j]));
                    }
                }
            }

            if (result is not (BlockImportResult.Imported or BlockImportResult.AlreadyKnown))
            {
                break;
            }
        }

        EndBackfill(block);
    }

    /// <summary>Releases the backfill of <paramref name="block"/>'s parent unless it imported or waits for a retry, so a later block may fetch it again within the slot's spent budget.</summary>
    private void EndBackfill(ForkedSignedBeaconBlock block)
    {
        Hash256 parent = block.ParentRoot;
        if (!_importer!.IsKnown(parent) && !_pendingRetry.ContainsKey(parent) && !IsWaitingForPayload(parent))
        {
            _backfilledParents.Remove(parent);
        }
    }

    /// <summary>Adds <paramref name="chain"/> to the fetch of <paramref name="root"/>, starting one off the worker unless one runs.</summary>
    private void StartAncestorFetch(Hash256 root, List<ForkedSignedBeaconBlock> chain, List<IBeaconSyncPeer?> sources, CancellationToken token)
    {
        if (_ancestorFetches.TryGetValue(root, out List<(List<ForkedSignedBeaconBlock> Chain, List<IBeaconSyncPeer?> Sources)>? waiting))
        {
            waiting.Add((chain, sources));
            return;
        }

        _ancestorFetches[root] = [(chain, sources)];
        _ = RunAncestorFetchAsync(root, token);
    }

    /// <remarks>Touches no worker state: the result is reported as an <see cref="AncestorFetchedItem"/>, also after a fault, so the fetch always frees its place.</remarks>
    private async Task RunAncestorFetchAsync(Hash256 root, CancellationToken token)
    {
        (ForkedSignedBeaconBlock Block, IBeaconSyncPeer Source)? fetched = null;
        try
        {
            fetched = await FetchBlockByRootAsync(root, token);
        }
        catch (Exception e)
        {
            if (!token.IsCancellationRequested && _logger.IsDebug) _logger.Debug($"By-root fetch of ancestor {root} failed and counts as not returned: {e.Message}");
        }

        try
        {
            await _work.Writer.WriteAsync(new AncestorFetchedItem(root, fetched?.Block, fetched?.Source), token);
        }
        catch (Exception e) when (e is OperationCanceledException or ChannelClosedException)
        {
        }
    }

    /// <summary>Resumes the chains waiting on the fetched ancestor, or holds their gossip blocks for range sync when no peer returned it.</summary>
    private async Task OnAncestorFetchedAsync(AncestorFetchedItem fetched, CancellationToken token)
    {
        if (!_ancestorFetches.Remove(fetched.Root, out List<(List<ForkedSignedBeaconBlock> Chain, List<IBeaconSyncPeer?> Sources)>? chains))
        {
            return;
        }

        // The ancestor may have imported, or begun to wait for its payload, by another route while the fetch ran.
        bool arrivedElsewhere = _importer!.IsKnown(fetched.Root) || IsWaitingForPayload(fetched.Root);
        foreach ((List<ForkedSignedBeaconBlock> chain, List<IBeaconSyncPeer?> sources) in chains)
        {
            if (!arrivedElsewhere)
            {
                if (fetched.Block is null)
                {
                    ForkedSignedBeaconBlock block = chain[0];
                    // phase0/p2p-interface.md beacon_block: a block whose parent is unseen MAY be queued until the parent is retrieved.
                    if (_logger.IsDebug) _logger.Debug($"No peer returned ancestor {fetched.Root} of gossip block at slot {block.Slot}; holding the block for range sync");
                    HoldRefusedBackfill(block);
                    WakeRangeSyncForAncestors();
                    EndBackfill(block);
                    continue;
                }

                chain.Add(fetched.Block);
                sources.Add(fetched.Source);
            }

            await AdvanceBackfillAsync(chain, sources, token);
        }
    }

    /// <summary>Tells the peer pool the chain reached <paramref name="slot"/>, capped at the wall slot, when that is past the head.</summary>
    /// <remarks>An unknown-parent gossip block is not signature-checked yet, so the cap keeps a forged slot from claiming more than the clock allows.</remarks>
    private void SignalChainAhead(ulong slot, string reason)
    {
        ulong reached = Math.Min(slot, slotClock.CurrentSlot);
        if (reached > (_lastHead?.HeadSlot ?? _syncTip.Slot))
        {
            peerPool.RefreshStatusesBehind(reached, reason);
        }
    }

    /// <summary>Starts the next range-sync round now instead of after the feed's wait, at most once per wall slot, so range sync fetches the ancestors no peer returned by root.</summary>
    /// <remarks>A round in flight is left running: an unverified gossip block must not be able to throw away a download in progress.</remarks>
    private void WakeRangeSyncForAncestors()
    {
        ulong slot = slotClock.CurrentSlot;
        if (_ancestorWakeSlot == slot)
        {
            return;
        }

        _ancestorWakeSlot = slot;
        // Cancelled asynchronously, so the feed's continuation does not run on the worker.
        _ = Interlocked.Exchange(ref _rangeSyncWake, new CancellationTokenSource()).CancelAsync();
    }

    /// <summary>Whether two blocks with the same message root also carry the same signature, so they are the same signed block.</summary>
    private static bool IsSameSignedBlock(ForkedSignedBeaconBlock first, ForkedSignedBeaconBlock second) =>
        ReferenceEquals(first, second) || (first, second) switch
        {
            (ForkedSignedBeaconBlock.OfFulu fulu, ForkedSignedBeaconBlock.OfFulu other) => fulu.Block.Signature == other.Block.Signature,
            (ForkedSignedBeaconBlock.OfGloas gloas, ForkedSignedBeaconBlock.OfGloas other) => gloas.Block.Signature == other.Block.Signature,
            _ => false,
        };

    private async Task<(ForkedSignedBeaconBlock Block, IBeaconSyncPeer Source)?> FetchBlockByRootAsync(Hash256 root, CancellationToken token)
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
                    return (block, peer);
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
        _headStepMs = slotClock.UnixMilliseconds;

        IBlockImporter importer = _importer!;
        HeadView head = importer.ComputeHead();
        if (_logger.IsDebug) _logger.Debug($"Head step: head {head.HeadRoot} at slot {head.HeadSlot}, sync tip slot {_syncTip.Slot}, justified epoch {head.Justified.Epoch}, finalized epoch {head.Finalized.Epoch}, wall slot {slotClock.CurrentSlot}");
        if (head.HeadExecutionHash is { } headExec)
        {
            PayloadStatusV1? status = await ForkchoiceUpdatedAsync(head, headExec, token);
            if (status?.Status == PayloadStatus.Invalid)
            {
                if (_logger.IsWarn) _logger.Warn($"Execution layer reported head {head.HeadRoot} INVALID (latest valid hash {status.LatestValidHash}); invalidating and re-running fork choice");
                importer.OnForkchoiceUpdated(head.HeadRoot, headExec, status);
                head = importer.ComputeHead();
                if (head.HeadExecutionHash is { } retryExec)
                {
                    headExec = retryExec;
                    status = await ForkchoiceUpdatedAsync(head, retryExec, token);
                    if (status?.Status == PayloadStatus.Invalid)
                    {
                        importer.OnForkchoiceUpdated(head.HeadRoot, headExec, status);
                        head = importer.ComputeHead();
                    }
                }
            }

            if (status is not null)
            {
                // specs/bellatrix/optimistic-sync.md: a NOT_VALIDATED head and its ancestors become VALID on the engine's answer.
                if (status.Status == PayloadStatus.Valid)
                {
                    importer.OnForkchoiceUpdated(head.HeadRoot, headExec, status);
                    head = importer.ComputeHead();
                }

                TrackExecutionSyncTransition(status);
            }
        }

        if (_lastHead is { } previous && head.Finalized.Epoch > previous.Finalized.Epoch)
        {
            if (_logger.IsInfo) _logger.Info($"FINALIZED epoch={head.Finalized.Epoch} root={head.Finalized.Root}");
            importer.OnFinalized(head.Finalized);
        }

        _lastHead = head;
        Metrics.BeaconChainHeadSlot = head.HeadSlot;
        Volatile.Write(ref _publishedHeadSlot, head.HeadSlot);
        RefreshHeadSlotDelay();
        Metrics.BeaconChainFinalizedEpoch = head.Finalized.Epoch;
        Metrics.BeaconChainJustifiedEpoch = head.Justified.Epoch;
        Metrics.BeaconChainElInSync = _elInSync ? 1 : 0;
        statusHolder.JustifiedRoot = head.Justified.Root;
        statusHolder.ExecutionInSync = _elInSync;
        StatusMessageV2 headStatus = new()
        {
            ForkDigest = _currentDigest,
            FinalizedRoot = head.Finalized.Root,
            FinalizedEpoch = head.Finalized.Epoch,
            HeadRoot = head.HeadRoot,
            HeadSlot = head.HeadSlot,
        };
        Hash256? fullHeadRoot = head.HeadPayloadFull ? head.HeadRoot : null;
        statusHolder.Publish(headStatus, fullHeadRoot);
        if (headSnapshots is not null) headSnapshots.Current = new HeadSnapshot(headStatus, fullHeadRoot, head.Justified.Root, _elInSync);

        // A payload attestation is accepted for the wall slot and, within the clock disparity, its neighbours.
        ulong wallSlot = slotClock.CurrentSlot;
        for (ulong slot = wallSlot == 0 ? 0 : wallSlot - 1; slot <= wallSlot + 1; slot++)
        {
            gossipRouter.SetPtc(slot, PtcReader is { } reader ? reader(head.HeadRoot, slot) : (importer as BlockImporter)?.GetPtc(head.HeadRoot, slot));
        }

        // Topics exist only once the libp2p host has started.
        if (!GossipStarted && p2p?.LocalPeerId is not null && head.HeadSlot + GossipStartDistanceSlots >= slotClock.CurrentSlot)
        {
            StartGossip();
        }
    }

    /// <summary>Points the execution layer at the anchor payload through <see cref="ForkchoiceUpdatedAsync"/>, so the first head step does not repeat an unchanged state.</summary>
    internal Task<PayloadStatusV1?> KickExecutionAsync(Hash256 anchorRoot)
    {
        CheckpointRef anchor = new(spec.GetEpoch(_anchorSlot), anchorRoot);
        return ForkchoiceUpdatedAsync(new HeadView(anchorRoot, _anchorSlot, _anchorExecutionHash, null, null, anchor, anchor), _anchorExecutionHash, CancellationToken.None);
    }

    /// <summary>Sends <c>forkchoiceUpdated</c> unless the same head, safe and finalized hashes were sent within <see cref="ForkchoiceResendInterval"/>.</summary>
    /// <remarks>
    /// The Engine API asks for the call when the fork choice state changes; an unchanged repeat each slot tells the EL nothing.
    /// It is still resent at that interval, as the EL treats a CL that sends neither this nor <c>newPayload</c> for a while as gone.
    /// A failed call returns <c>null</c> and is not remembered, so the next head step sends it again.
    /// </remarks>
    private async Task<PayloadStatusV1?> ForkchoiceUpdatedAsync(HeadView head, Hash256 headExec, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ForkchoiceHashes sent = new(headExec, head.JustifiedExecutionHash ?? headExec, head.FinalizedExecutionHash ?? _anchorExecutionHash);
        long now = slotClock.UnixMilliseconds;
        if (_lastForkchoice is { } last && last.Hashes == sent && last.Status.Status != PayloadStatus.Invalid
            && now - last.SentAtMs < (long)ForkchoiceResendInterval.TotalMilliseconds)
        {
            return last.Status;
        }

        PayloadStatusV1 status;
        try
        {
            status = await engine.ForkchoiceUpdated(sent.Head, sent.Safe, sent.Finalized).WaitAsync(token);
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            _elInSync = false;
            _lastForkchoice = null;
            if (_logger.IsWarn) _logger.Warn($"forkchoiceUpdated returned no status; it is sent again on the next head step: {e.Message}");
            return null;
        }

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

        // A peer offered on a stale status does not count, or the signalled slot would stop following the clock.
        if (_lastHead is { } lagging && lagging.HeadSlot + FollowingHeadSlackSlots < slot && !peerPool.GetBestPeers(lagging.HeadSlot + 1).Any(peer => peer.HeadSlot > lagging.HeadSlot))
        {
            SignalChainAhead(slot, $"head at slot {lagging.HeadSlot} is {slot - lagging.HeadSlot} slots behind the wall clock and no peer's status is past it");
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
            _logger.Info($"Beacon chain: head slot {head.HeadSlot} ({head.HeadRoot}), finalized epoch {head.Finalized.Epoch}, peers {peerManager?.PeerCount ?? 0}/{config.TargetPeerCount}, EL {(!engine.IsAvailable ? "unavailable" : _elInSync ? "in sync" : "syncing")}");
        }
    }

    /// <summary>Routes the gossip router's blocks and envelopes into the work channel and its votes and slashings into the vote channel; gossip overflow is droppable.</summary>
    internal void RouteGossipEvents()
    {
        gossipRouter.RequiresPtc = true;
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
        ulong newestTick = Volatile.Read(ref _newestSlotTick);
        // A vote stamped with a tick the worker has not reached cannot be read until then, so a tick stuck behind a full work channel must not let them fill the queue.
        bool ahead = newestTick > Volatile.Read(ref _reachedSlotTick);
        if (ahead && Interlocked.Increment(ref _votesAheadOfTick) > MaxVotesAheadOfTick)
        {
            Interlocked.Decrement(ref _votesAheadOfTick);
        }
        else if (_votes.Writer.TryWrite(new QueuedVote(vote, newestTick, ahead)))
        {
            WakeForVotes();
            return true;
        }
        else if (ahead)
        {
            Interlocked.Decrement(ref _votesAheadOfTick);
        }

        Interlocked.Increment(ref Metrics.GossipDroppedCount);
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

    private void RefreshHeadSlotDelay() =>
        Metrics.BeaconChainHeadSlotDelay = (long)slotClock.CurrentSlot - (long)Volatile.Read(ref _publishedHeadSlot);

    internal async Task RunHeadSlotDelayTimerAsync(CancellationToken token)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        do
        {
            RefreshHeadSlotDelay();
        } while (await timer.WaitForNextTickAsync(token));
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
            // Read before the round, so a wake during it also skips the wait after it.
            CancellationToken wake = Volatile.Read(ref _rangeSyncWake).Token;
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
            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(token, restart, wake);
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
        RangeSync.AnchorFallback? fallback = null;
        if (_rangeHeld is { } held && held.Tip.Slot > tip.Slot)
        {
            // Nothing past the held blocks can be held once the share is full, so fetching it would only repeat.
            if (Volatile.Read(ref _pendingCount) >= MaxRangeHeldBlocks)
            {
                return;
            }

            if (_logger.IsDebug) _logger.Debug($"Range sync round starts past the blocks held for a deferred block, at slot {held.Tip.Slot} instead of {tip.Slot}");
            // The held blocks are not signature-checked, so a round whose first block does not link to them starts over from the tip.
            fallback = new RangeSync.AnchorFallback(tip.Root, tip.Slot, () => { if (ReferenceEquals(_rangeHeld, held)) _rangeHeld = null; });
            tip = held.Tip;
        }

        if (slotClock.CurrentSlot <= tip.Slot)
        {
            return;
        }

        using CancellationTokenSource round = CancellationTokenSource.CreateLinkedTokenSource(token, restart);
        token = round.Token;

        List<ForkedSignedBeaconBlock.OfGloas> gloasRun = [];
        List<IBeaconSyncPeer> sources = [];
        IBeaconSyncPeer source = null!;
        await foreach (ForkedSignedBeaconBlock block in rangeSync.Run(tip.Root, tip.Slot, () => slotClock.CurrentSlot, token, fallback, peer => source = peer, retryToken => WriteGloasRunAsync(gloasRun, sources, restart, retryToken)))
        {
            if (block is not ForkedSignedBeaconBlock.OfGloas gloas)
            {
                await WriteGloasRunAsync(gloasRun, sources, restart, token);
                await _work.Writer.WriteAsync(new RangeBlockItem(block, source, restart), token);
                continue;
            }

            if (gloasRun.Count > 0 && gloas.Slot - gloasRun[0].Slot >= ExecutionPayloadEnvelopesProtocolBase.MaxRequestPayloads)
            {
                await WriteGloasRunAsync(gloasRun, sources, restart, token);
            }

            gloasRun.Add(gloas);
            sources.Add(source);
            if ((ulong)gloasRun.Count >= RangeSync.DefaultBatchSize)
            {
                await WriteGloasRunAsync(gloasRun, sources, restart, token);
            }
        }

        await WriteGloasRunAsync(gloasRun, sources, restart, token);
    }

    /// <summary>Writes each block of <paramref name="run"/> in order, each followed by its envelope when the chain carries that payload, then clears the run.</summary>
    /// <remarks>
    /// A block's payload is on the chain when the next block's bid builds on it (<c>bid.parent_block_hash</c> equals its
    /// <c>bid.block_hash</c>); the last block's envelope is written when a peer served it. A missing envelope a full child
    /// needs is recovered by root once that child is parked.
    /// </remarks>
    private async Task WriteGloasRunAsync(List<ForkedSignedBeaconBlock.OfGloas> run, List<IBeaconSyncPeer> sources, CancellationToken restart, CancellationToken token)
    {
        if (run.Count == 0)
        {
            return;
        }

        (SignedExecutionPayloadEnvelope Envelope, IBeaconSyncPeer Source)?[] envelopes = await FetchRunEnvelopesAsync(run, token);
        for (int i = 0; i < run.Count; i++)
        {
            await _work.Writer.WriteAsync(new RangeBlockItem(run[i], sources[i], restart), token);
            if (envelopes[i] is { } served && (i == run.Count - 1 || IsPayloadOnChain(run[i], run[i + 1])))
            {
                await _work.Writer.WriteAsync(new RangeEnvelopeItem(served.Envelope, served.Source), token);
            }
        }

        run.Clear();
        sources.Clear();
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
        Hash256 firstRoot = run[0].ComputeMessageRoot();
        if (!_rangeEnvelopePeerSelections.TryGet(firstRoot, out RangeSync.ColumnFetchRotation? rotation))
        {
            rotation = new RangeSync.ColumnFetchRotation(slotClock);
            _rangeEnvelopePeerSelections.Set(firstRoot, rotation);
        }
        peers = rotation.Take(peers, MaxBackfillPeersPerRequest);
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

            bool[] served = new bool[run.Count];
            bool invalid = false;
            foreach (SignedExecutionPayloadEnvelope envelope in envelopes)
            {
                if (envelope.Message is not { BeaconBlockRoot: { } root } message
                    || !indexByRoot.TryGetValue(root, out int index)
                    || !MatchesBid(message, run[index]))
                {
                    peer.ReportFailure(PeerFailureReason.ProtocolViolation, $"Execution-payload-envelopes-by-range from slot {startSlot} returned an envelope for no block of the range");
                    invalid = true;
                    continue;
                }

                served[index] = true;
                found[index] ??= (envelope, peer);
            }

            // Networking BeaconBlocksByRange, which this request follows: a reply MAY stop early, but MUST NOT skip one that exists.
            // A served envelope puts its block, and so every earlier block of the run, on the peer's chain, where the same bids decide each payload.
            int lastServed = Array.LastIndexOf(served, true);
            for (int i = 0; !invalid && i < lastServed; i++)
            {
                if (!served[i] && run[i].Slot >= peer.EarliestAvailableSlot && IsPayloadOnChain(run[i], run[i + 1]))
                {
                    peer.ReportFailure(PeerFailureReason.ProtocolViolation, $"Execution-payload-envelopes-by-range from slot {startSlot} skipped the on-chain payload of slot {run[i].Slot}");
                    break;
                }
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

    /// <summary>Dials discovered candidates until cancellation, with scheduling owned by the peer manager.</summary>
    private Task RunDiscoveryDialLoopAsync(CancellationToken token)
        => peerManager!.DialDiscoveredPeersAsync(discovery!.DiscoverPeers(token), token);

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
        _logger.Info($"Beacon sync: slot {slot} (+{slot - _progressLogSlot} slots, {_blocksSinceProgressLog / seconds:F1} blocks/s, {_importMsSinceProgressLog / _blocksSinceProgressLog} ms/block of which newPayload {(long)(newPayloadMs - _newPayloadMsAtProgressLog) / _blocksSinceProgressLog} ms), {behind} behind wall slot {wallSlot}, finalized epoch {_lastHead?.Finalized.Epoch ?? 0}, peers {peerManager?.PeerCount ?? 0}/{config.TargetPeerCount}, EL {(!engine.IsAvailable ? "unavailable" : _elInSync ? "in sync" : "syncing")}");
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

    /// <summary>Runs blocking import work, one item at a time, on a thread that is not a thread-pool thread.</summary>
    /// <remarks>
    /// The importer's newPayload call blocks its thread until the execution layer answers, which can take minutes under load,
    /// so it must not hold a pool thread. The thread starts on the first item and ends after <see cref="IdleTimeout"/> without one,
    /// so an instance nobody uses holds no thread and needs no disposal.
    /// </remarks>
    internal sealed class ImportThread
    {
        // Two slots, so gossip blocks 12 s apart keep one thread and its thread-local caches.
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(24);

        private readonly object _gate = new();
        private readonly Queue<Action> _queue = new();
        private readonly Action<Thread> _start;
        private bool _running;

        /// <param name="start">Starts the import thread; replaced by tests to fail it.</param>
        internal ImportThread(Action<Thread>? start = null) => _start = start ?? (static thread => thread.Start());

        /// <returns>A task with the result or the exception of <paramref name="work"/>, whose continuations never run on the import thread.</returns>
        public Task<T> RunAsync<T>(Func<T> work)
        {
            TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _queue.Enqueue(() =>
                {
                    try
                    {
                        completion.SetResult(work());
                    }
                    catch (Exception e)
                    {
                        completion.SetException(e);
                    }
                });

                if (_running)
                {
                    Monitor.Pulse(_gate);
                }
                else
                {
                    _running = true;
                    try
                    {
                        _start(new Thread(Run) { IsBackground = true, Name = "Beacon block import" });
                    }
                    catch
                    {
                        // No thread will drain the queue, so the next call must start one instead of waiting for it.
                        _running = false;
                        _queue.Clear();
                        throw;
                    }
                }
            }

            return completion.Task;
        }

        private void Run()
        {
            while (true)
            {
                Action work;
                lock (_gate)
                {
                    while (!_queue.TryDequeue(out work!))
                    {
                        if (!Monitor.Wait(_gate, IdleTimeout) && _queue.Count == 0)
                        {
                            _running = false;
                            return;
                        }
                    }
                }

                work();
            }
        }
    }
}
