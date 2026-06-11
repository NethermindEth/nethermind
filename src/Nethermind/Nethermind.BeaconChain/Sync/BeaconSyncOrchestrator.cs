// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.BeaconChain.Sync;

/// <summary>
/// Drives the embedded beacon chain to follow mainnet: kicks the execution layer toward the
/// checkpoint anchor, replays stored canonical blocks, range-syncs to the wall clock, then follows
/// gossip — funneling all consensus work through a single import worker.
/// </summary>
/// <remarks>
/// Threading model: producers (gossip events on libp2p threads, the slot timer, the range-sync
/// feed) only write to a bounded channel; one worker loop consumes it and is the only thread that
/// touches the <see cref="IBlockImporter"/> (and through it the state transition and fork choice,
/// neither of which is thread-safe). A fork-choice head step — <c>engine_forkchoiceUpdated</c>,
/// finality handling, status refresh — runs on the worker after every drained import batch (or
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
    BeaconDiscovery? discovery = null)
{
    /// <summary>Maximum parent-chain depth fetched by root for a gossip block with an unknown parent.</summary>
    private const int MaxBackfillDepth = 32;

    /// <summary>Peers tried per by-root fetch before giving the block up.</summary>
    private const int MaxBackfillPeersPerRequest = 3;

    private const int MaxPendingGossipBlocks = 128;
    private const int WorkQueueCapacity = 512;

    /// <summary>Head-step cadence while the work queue never drains (deep range sync).</summary>
    private const int HeadStepImportInterval = 64;

    /// <summary>Head distance (~2 epochs) below which gossip is started while range sync finishes the residual gap.</summary>
    private const ulong GossipStartDistanceSlots = 64;

    private const int ConcurrentDials = 8;

    private readonly ILogger _logger = logManager.GetClassLogger<BeaconSyncOrchestrator>();
    private readonly Channel<WorkItem> _work = Channel.CreateBounded<WorkItem>(
        new BoundedChannelOptions(WorkQueueCapacity) { SingleReader = true });

    /// <summary>First-block-per-(slot, proposer) gossip rule.</summary>
    private readonly LruKeyCache<(ulong Slot, ulong Proposer)> _seenProposals = new(1024, "beacon gossip proposals");

    /// <summary>Gossip blocks waiting for their parent, keyed by the unknown parent root.</summary>
    private readonly Dictionary<Hash256, List<SignedBeaconBlock>> _pendingByParent = [];

    private readonly ConcurrentDictionary<string, byte> _dialedPeerIds = new();
    private readonly Stopwatch _progressStopwatch = new();

    private IBlockImporter? _importer;
    private volatile Tip _syncTip = new(Hash256.Zero, 0);
    private Hash256 _anchorExecutionHash = Hash256.Zero;
    private ulong _anchorSlot;
    private HeadView? _lastHead;
    private bool _elInSync;
    private bool _importedSinceHeadStep;
    private int _importsSinceHeadStep;
    private int _pendingCount;
    private byte[] _currentDigest = [];
    private (ulong Epoch, byte[] Digest)? _nextRotation;
    private ulong _nextProgressLogSlot;
    private long _blocksSinceProgressLog;

    private sealed record Tip(Hash256 Root, ulong Slot);

    internal abstract record WorkItem;
    internal sealed record RangeBlockItem(SignedBeaconBlock Block) : WorkItem;
    internal sealed record GossipBlockItem(SignedBeaconBlock Block) : WorkItem;
    internal sealed record GossipAggregateItem(SignedAggregateAndProof Aggregate) : WorkItem;
    internal sealed record GossipAttesterSlashingItem(AttesterSlashing Slashing) : WorkItem;
    internal sealed record SlotTickItem(ulong Slot) : WorkItem;

    /// <summary>Whether gossip topics are subscribed; settable by tests to exercise the rotation path.</summary>
    internal bool GossipStarted { get; set; }

    internal byte[] CurrentGossipDigest => _currentDigest;

    internal (Hash256 Root, ulong Slot) SyncTip => (_syncTip.Root, _syncTip.Slot);

    internal ChannelWriter<WorkItem> WorkWriter => _work.Writer;

    /// <summary>Runs the full sync flow from the given anchor until cancelled.</summary>
    public async Task RunAsync(BeaconStateFulu anchorState, SignedBeaconBlock anchorBlock, Hash256 anchorRoot, CancellationToken token)
    {
        if (p2p is null || peerManager is null || discovery is null)
        {
            throw new InvalidOperationException($"{nameof(BeaconSyncOrchestrator)} requires the P2P components to run");
        }

        Initialize(importerFactory.Create(anchorState, anchorBlock, anchorRoot), anchorBlock, anchorRoot);

        // Engine kick: point the execution layer at the anchor payload so it starts beacon/snap
        // syncing toward it; SYNCING is the expected (successful) answer here.
        if (_logger.IsInfo) _logger.Info($"Beacon sync starting from anchor slot {_anchorSlot} ({anchorRoot}); kicking execution layer with forkchoiceUpdated(head=safe=finalized={_anchorExecutionHash})");
        PayloadStatusV1 kick = await engine.ForkchoiceUpdated(_anchorExecutionHash, _anchorExecutionHash, _anchorExecutionHash);
        if (_logger.IsInfo) _logger.Info($"Engine kick returned {kick.Status}{(kick.Status == PayloadStatus.Syncing ? " — execution layer is syncing toward the anchor" : "")}");

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
    internal void Initialize(IBlockImporter importer, SignedBeaconBlock anchorBlock, Hash256 anchorRoot)
    {
        _importer = importer;
        _anchorSlot = anchorBlock.Message!.Slot;
        _anchorExecutionHash = anchorBlock.Message.Body!.ExecutionPayload!.BlockHash!;
        _syncTip = new Tip(anchorRoot, _anchorSlot);
        _nextProgressLogSlot = _anchorSlot + spec.SlotsPerEpoch;
        _progressStopwatch.Restart();

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
            EarliestAvailableSlot = _anchorSlot,
        };
    }

    /// <summary>
    /// Re-imports the canonical blocks already persisted between the anchor and the wall clock, so
    /// a restart does not refetch them from the network.
    /// </summary>
    /// <remarks>
    /// Blocks are imported with signature verification off: they were fully verified before being
    /// persisted. Parent linkage is still checked so a stale index tail (e.g. entries past an
    /// unfinalized reorg point) stops the replay and leaves the rest to range sync.
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
            if (!store.TryGetCanonicalRoot(slot, out Hash256? root) || !store.TryGetBlock(root, out SignedBeaconBlock? block))
            {
                continue;
            }

            if (block.Message!.ParentRoot != expectedParent || importer.Import(block, root, verifySignatures: false) != BlockImportResult.Imported)
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

    /// <summary>The single consumer of the work channel; completes when the channel is completed or the token fires.</summary>
    internal async Task RunWorkerAsync(CancellationToken token)
    {
        ChannelReader<WorkItem> reader = _work.Reader;
        while (await reader.WaitToReadAsync(token))
        {
            while (reader.TryRead(out WorkItem? item))
            {
                await ProcessItemAsync(item, token);
            }

            if (_importedSinceHeadStep)
            {
                await RunHeadStepAsync(token);
            }
        }
    }

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
            case GossipAttesterSlashingItem slashing:
                _importer!.OnGossipAttesterSlashing(slashing.Slashing);
                break;
            case SlotTickItem tick:
                await ProcessSlotAsync(tick.Slot, token);
                break;
        }
    }

    /// <summary>Imports one block and, on success, drains any gossip blocks that were waiting for it.</summary>
    internal async Task<BlockImportResult> ImportBlockAsync(SignedBeaconBlock block, CancellationToken token)
    {
        Hash256 root = SszRoots.HashTreeRoot(block.Message!);
        BlockImportResult result = _importer!.Import(block, root, verifySignatures: true);
        if (result == BlockImportResult.Imported)
        {
            await OnImportedAsync(root, block.Message!.Slot, token);
        }

        return result;
    }

    private async Task OnImportedAsync(Hash256 root, ulong slot, CancellationToken token)
    {
        if (slot > _syncTip.Slot)
        {
            _syncTip = new Tip(root, slot);
        }

        _importedSinceHeadStep = true;
        LogSyncProgress(slot);

        if (_pendingByParent.Remove(root, out List<SignedBeaconBlock>? children))
        {
            _pendingCount -= children.Count;
            foreach (SignedBeaconBlock child in children)
            {
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
    /// finalized slot, first block per (slot, proposer), expected proposer per the lookahead.
    /// The proposer signature is verified by the state transition during the immediate import
    /// (the import runs with <c>verifySignatures: true</c> right below), so no separate
    /// pre-verification pass is needed.
    /// </summary>
    internal async Task ProcessGossipBlockAsync(SignedBeaconBlock block, CancellationToken token)
    {
        IBlockImporter importer = _importer!;
        BeaconBlock message = block.Message!;
        Hash256 root = SszRoots.HashTreeRoot(message);
        if (importer.IsKnown(root))
        {
            return;
        }

        if (_lastHead is { } head && message.Slot <= BeaconStateAccessors.ComputeStartSlotAtEpoch(head.Finalized.Epoch))
        {
            return;
        }

        if (!_seenProposals.Set((message.Slot, message.ProposerIndex)))
        {
            if (_logger.IsDebug) _logger.Debug($"Ignoring repeat gossip proposal for slot {message.Slot} by proposer {message.ProposerIndex}");
            return;
        }

        if (!importer.IsExpectedProposer(block))
        {
            if (_logger.IsWarn) _logger.Warn($"Dropping gossip block at slot {message.Slot} with unexpected proposer {message.ProposerIndex}");
            return;
        }

        if (!importer.IsKnown(message.ParentRoot!))
        {
            // While far behind, range sync will deliver the parent chain anyway — just hold the
            // block; in steady state fetch the missing ancestors by root.
            if (_syncTip.Slot + MaxBackfillDepth < slotClock.CurrentSlot)
            {
                QueuePendingGossipBlock(block);
            }
            else
            {
                await BackfillAndImportAsync(block, token);
            }

            return;
        }

        await ImportBlockAsync(block, token);
    }

    private void QueuePendingGossipBlock(SignedBeaconBlock block)
    {
        if (_pendingCount >= MaxPendingGossipBlocks)
        {
            return;
        }

        Hash256 parent = block.Message!.ParentRoot!;
        if (!_pendingByParent.TryGetValue(parent, out List<SignedBeaconBlock>? siblings))
        {
            _pendingByParent[parent] = siblings = [];
        }

        siblings.Add(block);
        _pendingCount++;
    }

    /// <summary>Fetches the unknown parent chain of a gossip block by root (bounded depth), then imports oldest-first.</summary>
    private async Task BackfillAndImportAsync(SignedBeaconBlock block, CancellationToken token)
    {
        IBlockImporter importer = _importer!;
        List<SignedBeaconBlock> chain = [block];
        Hash256 parent = block.Message!.ParentRoot!;
        while (!importer.IsKnown(parent))
        {
            if (chain.Count > MaxBackfillDepth)
            {
                if (_logger.IsDebug) _logger.Debug($"Giving up on gossip block at slot {block.Message.Slot}: ancestor chain exceeds {MaxBackfillDepth} unknown blocks");
                return;
            }

            SignedBeaconBlock? fetched = await FetchBlockByRootAsync(parent, token);
            if (fetched is null)
            {
                if (_logger.IsDebug) _logger.Debug($"Giving up on gossip block at slot {block.Message.Slot}: no peer returned ancestor {parent}");
                return;
            }

            chain.Add(fetched);
            parent = fetched.Message!.ParentRoot!;
        }

        for (int i = chain.Count - 1; i >= 0; i--)
        {
            if (await ImportBlockAsync(chain[i], token) is not (BlockImportResult.Imported or BlockImportResult.AlreadyKnown))
            {
                return;
            }
        }
    }

    private async Task<SignedBeaconBlock?> FetchBlockByRootAsync(Hash256 root, CancellationToken token)
    {
        IReadOnlyList<IBeaconSyncPeer> peers = peerPool.GetBestPeers(0);
        for (int i = 0; i < peers.Count && i < MaxBackfillPeersPerRequest; i++)
        {
            IBeaconSyncPeer peer = peers[i];
            IReadOnlyList<SignedBeaconBlock> blocks;
            try
            {
                blocks = await peer.RequestBlocksByRootAsync([root], token);
            }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
            {
                // Includes per-request timeouts, which cancel the request without cancelling the sync.
                peer.ReportFailure($"Blocks-by-root for {root} failed: {e.Message}");
                continue;
            }

            foreach (SignedBeaconBlock block in blocks)
            {
                if (SszRoots.HashTreeRoot(block.Message!) == root)
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
        statusHolder.CurrentStatus = new StatusMessageV2
        {
            ForkDigest = _currentDigest,
            FinalizedRoot = head.Finalized.Root,
            FinalizedEpoch = head.Finalized.Epoch,
            HeadRoot = head.HeadRoot,
            HeadSlot = head.HeadSlot,
            EarliestAvailableSlot = _anchorSlot,
        };

        if (!GossipStarted && head.HeadSlot + GossipStartDistanceSlots >= slotClock.CurrentSlot)
        {
            StartGossip();
        }
    }

    private Task<PayloadStatusV1> ForkchoiceUpdatedAsync(HeadView head, Hash256 headExec) =>
        engine.ForkchoiceUpdated(
            headExec,
            head.JustifiedExecutionHash ?? headExec,
            head.FinalizedExecutionHash ?? _anchorExecutionHash);

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
        _importer!.OnSlotTick(slot);
        await RunHeadStepAsync(token);

        ulong epoch = spec.GetEpoch(slot);
        if (_nextRotation is { } rotation && epoch >= rotation.Epoch)
        {
            _currentDigest = rotation.Digest;
            _nextRotation = GossipTopics.NextRotation(spec, rotation.Epoch);
            if (GossipStarted)
            {
                gossipRouter.RotateDigest(rotation.Digest);
            }

            discovery?.UpdateLocalEnr();
            if (_logger.IsInfo) _logger.Info($"Rotated beacon gossip fork digest to 0x{Convert.ToHexStringLower(rotation.Digest)} at epoch {epoch}");
        }

        if (slot % spec.SlotsPerEpoch == 0 && _lastHead is { } head && _logger.IsInfo)
        {
            _logger.Info($"Beacon chain: head slot {head.HeadSlot} ({head.HeadRoot}), finalized epoch {head.Finalized.Epoch}, peers {peerManager?.PeerCount ?? 0}, EL {(_elInSync ? "in sync" : "syncing")}");
        }
    }

    /// <summary>Subscribes the gossip topics and routes their events into the work channel; gossip overflow is droppable.</summary>
    private void StartGossip()
    {
        GossipStarted = true;
        gossipRouter.BeaconBlockReceived += block => _work.Writer.TryWrite(new GossipBlockItem(block));
        gossipRouter.AggregateAndProofReceived += aggregate => _work.Writer.TryWrite(new GossipAggregateItem(aggregate));
        gossipRouter.AttesterSlashingReceived += slashing => _work.Writer.TryWrite(new GossipAttesterSlashingItem(slashing));
        gossipRouter.Start(p2p!.GetTopic, _currentDigest);
        if (_logger.IsInfo) _logger.Info($"Within {GossipStartDistanceSlots} slots of the wall clock — gossip following started");
    }

    private async Task PumpSlotTicksAsync(CancellationToken token)
    {
        await foreach (ulong slot in slotClock.SlotTicks(token))
        {
            await _work.Writer.WriteAsync(new SlotTickItem(slot), token);
        }
    }

    /// <summary>Feeds range-synced blocks into the work channel, re-running as the wall clock outpaces the sync tip.</summary>
    private async Task RunRangeSyncFeedAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Tip tip = _syncTip;
            try
            {
                if (slotClock.CurrentSlot > tip.Slot)
                {
                    await foreach (SignedBeaconBlock block in rangeSync.Run(tip.Root, tip.Slot, () => slotClock.CurrentSlot, token))
                    {
                        await _work.Writer.WriteAsync(new RangeBlockItem(block), token);
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
            {
                // The feed must survive network-level failures; the next round restarts from the tip.
                if (_logger.IsDebug) _logger.Debug($"Range sync round failed and will be retried: {e.Message}");
            }

            // Caught up (or briefly stalled): in steady state gossip keeps the tip moving and this
            // loop only re-checks for gaps once per slot.
            await Task.Delay(TimeSpan.FromSeconds(spec.SecondsPerSlot), token);
        }
    }

    /// <summary>Dials discovered candidates (bounded concurrency) until the target peer count is reached, then idles.</summary>
    private async Task RunDiscoveryDialLoopAsync(CancellationToken token)
    {
        using SemaphoreSlim dialGate = new(ConcurrentDials);
        await foreach (BeaconPeerCandidate candidate in discovery!.DiscoverPeers(token))
        {
            while (peerManager!.PeerCount >= config.TargetPeerCount)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }

            if (!_dialedPeerIds.TryAdd(candidate.PeerId, 0))
            {
                continue;
            }

            await dialGate.WaitAsync(token);
            _ = DialCandidateAsync(candidate, dialGate, token);
        }
    }

    private async Task DialCandidateAsync(BeaconPeerCandidate candidate, SemaphoreSlim dialGate, CancellationToken token)
    {
        try
        {
            if (!await peerManager!.TryAddPeerAsync(candidate.Multiaddress, token))
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

    private void LogSyncProgress(ulong slot)
    {
        _blocksSinceProgressLog++;
        ulong wallSlot = slotClock.CurrentSlot;
        if (slot + GossipStartDistanceSlots >= wallSlot || slot < _nextProgressLogSlot)
        {
            return;
        }

        double blocksPerSecond = _blocksSinceProgressLog / Math.Max(_progressStopwatch.Elapsed.TotalSeconds, 0.001);
        if (_logger.IsInfo) _logger.Info($"Beacon sync: slot {slot}/{wallSlot} ({wallSlot - slot} behind), {blocksPerSecond:F1} blocks/s");
        _nextProgressLogSlot = slot + spec.SlotsPerEpoch;
        _blocksSinceProgressLog = 0;
        _progressStopwatch.Restart();
    }
}
