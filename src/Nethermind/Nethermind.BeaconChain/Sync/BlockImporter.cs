// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Sync;

/// <summary>
/// The production <see cref="IBlockImporter"/>: state transition over a followed canonical lineage,
/// proto-array fork choice, engine <c>newPayload</c> via the transition hook, and persistence.
/// </summary>
/// <remarks>
/// <para>
/// State lineage policy: one live <see cref="BeaconStateFulu"/> follows the canonical chain with a
/// single <see cref="CachedBeaconStateHasher"/> (in the lineage <see cref="EpochCache"/>); rare
/// fork branches are processed on clones with a fresh cache and the stateless
/// <see cref="FullBeaconStateHasher"/>, and the lineage (with a fresh cached hasher) is re-adopted
/// from the retained post-state when fork choice reorgs the head. Untrusted blocks (signature
/// verification on) are applied to a clone of the lineage state so an invalid block cannot leave
/// the lineage partially mutated; trusted store replays are applied in place.
/// </para>
/// <para>
/// Post-states around epoch boundaries (both the last block of an epoch and the first block of the
/// next) are retained in <see cref="PostStateCache"/> so fork choice can resolve checkpoint states
/// regardless of whether the epoch's first slot was skipped. Not thread-safe; all calls must come
/// from the orchestrator's import worker.
/// </para>
/// </remarks>
public sealed class BlockImporter : IBlockImporter
{
    private static readonly StringLabel BodyAttestationRejected = new("body_attestation");
    private static readonly StringLabel BodyAttesterSlashingRejected = new("body_attester_slashing");
    private static readonly StringLabel GossipAggregateRejected = new("gossip_aggregate");
    private static readonly StringLabel GossipAttesterSlashingRejected = new("gossip_attester_slashing");

    private readonly BeaconChainSpec _spec;
    private readonly BeaconChainStore _store;
    private readonly PubkeyCache _pubkeys;
    private readonly IEngineDriver _engine;
    private readonly IBeaconChainConfig _config;
    private readonly ILogger _logger;

    /// <summary>The <c>is_data_available</c> rule for blocks from the network; store replays use <see cref="ReplayedBlockAvailability"/>.</summary>
    private readonly IDataAvailabilityRule _availability;

    private readonly PostStateCache _states;
    private readonly ForkChoiceRunner _runner;
    private readonly ForkChoiceSnapshotHolder? _forkChoiceSnapshots;

    /// <summary>Imported-but-not-finalized block roots and their slots, for store pruning at finalization.</summary>
    private readonly Dictionary<Hash256, ulong> _unfinalized = [];

    private EpochCache _lineageCache = new() { Hasher = new CachedBeaconStateHasher() };

    /// <summary>Whether the current lineage head block is the first block of its epoch (its post-state is then a checkpoint-root candidate).</summary>
    private bool _lineageBlockStartsEpoch = true;

    private Hash256 _canonicalHead;
    private ulong _lastSnapshotEpoch;

    /// <param name="forkChoiceSnapshots">Where <see cref="ComputeHead"/> publishes a copy of the fork-choice store for readers off the import thread; <c>null</c> publishes nothing.</param>
    public BlockImporter(
        BeaconChainSpec spec,
        BeaconChainStore store,
        PubkeyCache pubkeys,
        IEngineDriver engine,
        IBeaconChainConfig config,
        ILogManager logManager,
        IDataAvailabilityRule availability,
        BeaconStateFulu anchorState,
        SignedBeaconBlock anchorBlock,
        Hash256 anchorRoot,
        ForkChoiceSnapshotHolder? forkChoiceSnapshots = null)
    {
        _spec = spec;
        _store = store;
        _pubkeys = pubkeys;
        _engine = engine;
        _config = config;
        _logger = logManager.GetClassLogger<BlockImporter>();
        _availability = availability;
        _forkChoiceSnapshots = forkChoiceSnapshots;

        _states = new PostStateCache(store, spec, anchorRoot, anchorState);
        _runner = new ForkChoiceRunner(spec, anchorState, anchorBlock.Message!, _states, pubkeys);
        _canonicalHead = anchorRoot;
        _lastSnapshotEpoch = anchorState.GetCurrentEpoch();
        store.SetCanonicalRoot(anchorBlock.Message!.Slot, anchorRoot);
    }

    /// <inheritdoc/>
    public bool IsKnown(Hash256 blockRoot) => _runner.ContainsBlock(blockRoot);

    /// <inheritdoc/>
    public bool IsExpectedProposer(SignedBeaconBlock block)
    {
        BeaconBlock message = block.Message!;
        BeaconStateFulu state = _states.LineageState;
        ulong epoch = BeaconStateAccessors.ComputeEpochAtSlot(message.Slot);
        ulong stateEpoch = state.GetCurrentEpoch();
        if (epoch != stateEpoch && epoch != stateEpoch + 1)
        {
            return true;
        }

        return state.GetBeaconProposerIndex(message.Slot) == message.ProposerIndex;
    }

    /// <inheritdoc/>
    public BlockImportResult Import(SignedBeaconBlock signedBlock, Hash256 blockRoot, bool verifySignatures)
    {
        BeaconBlock block = signedBlock.Message!;
        if (_runner.ContainsBlock(blockRoot))
        {
            return BlockImportResult.AlreadyKnown;
        }

        Hash256 parentRoot = block.ParentRoot!;
        if (!_runner.ContainsBlock(parentRoot))
        {
            return BlockImportResult.UnknownParent;
        }

        // The spec's on_block asserts is_data_available before state_transition; checking it here,
        // ahead of the clone and the engine call, means a block trailing its columns costs nothing
        // to defer and never reaches the engine. A stored block passed this gate before it was
        // persisted, and the columns that satisfied it are not persisted, so a trusted replay can
        // neither re-check them nor needs to.
        IDataAvailabilityRule availability = verifySignatures ? _availability : ReplayedBlockAvailability.Instance;
        if (!availability.IsDataAvailable(block, blockRoot, _spec))
        {
            if (_logger.IsWarn) _logger.Warn($"Deferring block {blockRoot} at slot {block.Slot}: blob data is not yet available");
            return BlockImportResult.DataUnavailable;
        }

        bool onLineage = parentRoot == _states.LineageRoot;
        BeaconStateFulu? parentState = onLineage ? _states.LineageState : _states.CopyBlockState(parentRoot);
        if (parentState is null)
        {
            // The parent is known to fork choice but its post-state fell out of all retention
            // tiers (a deep fork point); the branch cannot be processed.
            if (_logger.IsWarn) _logger.Warn($"Cannot import block at slot {block.Slot}: the post-state of its parent {parentRoot} is no longer retained");
            return BlockImportResult.UnknownParent;
        }

        ulong parentEpoch = parentState.GetCurrentEpoch();
        ulong blockEpoch = BeaconStateAccessors.ComputeEpochAtSlot(block.Slot);
        bool crossesEpoch = blockEpoch > parentEpoch;

        BeaconStateFulu state;
        EpochCache cache;
        if (onLineage)
        {
            // Untrusted blocks run on a clone so an invalid block cannot corrupt the live lineage
            // state; trusted replays were validated before persisting and apply in place.
            state = verifySignatures ? parentState.Clone() : parentState;
            cache = _lineageCache;
            if (!verifySignatures && (crossesEpoch || _lineageBlockStartsEpoch))
            {
                _states.Retain(parentRoot, parentState.Clone());
            }
        }
        else
        {
            state = parentState; // CopyBlockState already cloned
            cache = new EpochCache(); // fork branch: stateless hasher, fresh balance memo
        }

        ImportVerdict verdict = new(_engine);
        _engine.CurrentBlock = signedBlock;
        try
        {
            // A transition that runs in place cannot be abandoned part-way: ProcessBlockHeader has
            // already advanced LatestBlockHeader by the time the engine is called, so a retry of
            // the same block would fail its own header check forever. Drive newPayload before
            // anything is mutated. A cloned transition can abort for free, and keeping the call in
            // the hook there leaves the spec's validation order intact for untrusted blocks.
            if (ReferenceEquals(state, parentState) && onLineage)
            {
                verdict.Prime(block.Body!);
            }

            StateTransition.StateTransition.Apply(state, signedBlock, cache, _pubkeys, verdict, _spec, validateResult: true, verifySignatures);
        }
        catch (EngineUnavailableException e)
        {
            if (_logger.IsWarn) _logger.Warn($"Deferring block {blockRoot} at slot {block.Slot}: {e.Message}");
            return BlockImportResult.EngineUnavailable;
        }
        catch (BeaconStateException e)
        {
            if (_logger.IsWarn) _logger.Warn($"Dropping invalid block {blockRoot} at slot {block.Slot}: {e.Message}");
            return BlockImportResult.Invalid;
        }
        finally
        {
            _engine.CurrentBlock = null;
        }

        // The transition hook already drove engine_newPayload; an INVALID verdict made Apply throw.
        ExecutionStatus executionStatus = verdict.Status;
        OnSlotTick(block.Slot); // a timely gossip block can be marginally ahead of the last tick

        // Already confirmed available above; this is a harmless backstop for a caller of OnBlock
        // that does not pre-check (the consensus-spec vector harness calls it directly).
        try
        {
            _runner.OnBlock(signedBlock, state, executionStatus, availability);
        }
        catch (ForkChoiceException e)
        {
            if (_logger.IsWarn) _logger.Warn($"Dropping block {blockRoot} at slot {block.Slot} rejected by fork choice: {e.Message}");
            return BlockImportResult.Invalid;
        }

        if (executionStatus is ExecutionStatus.Valid)
        {
            _runner.OnValidExecutionPayload(blockRoot);
        }

        ApplyBodyOperations(block.Body!);

        _store.PutBlock(blockRoot, signedBlock);
        _unfinalized[blockRoot] = block.Slot;

        if (onLineage)
        {
            if (verifySignatures && (crossesEpoch || _lineageBlockStartsEpoch))
            {
                // The pre-clone original is the parent post-state, retained as-is.
                _states.Retain(parentRoot, parentState);
            }

            _states.SetLineage(blockRoot, state);
            _lineageBlockStartsEpoch = crossesEpoch;
            MaybeSnapshotState(blockRoot, state, blockEpoch);
        }
        else
        {
            _states.Retain(blockRoot, state);
        }

        return BlockImportResult.Imported;
    }

    /// <inheritdoc/>
    public void OnSlotTick(ulong slot)
    {
        ulong time = _runner.GenesisTime + slot * _spec.SecondsPerSlot;
        if (time > _runner.Time)
        {
            _runner.OnTick(time);
        }
    }

    /// <inheritdoc/>
    public HeadView ComputeHead()
    {
        Hash256 head = _runner.GetHead();
        // After GetHead, so the copy carries the weights this head was chosen by.
        _forkChoiceSnapshots?.Current = _runner.Snapshot();
        AdoptHeadLineage(head);
        UpdateCanonicalIndex(head);
        return new HeadView(
            head,
            _runner.GetBlockSlot(head) ?? 0,
            _runner.GetExecutionBlockHash(head),
            _runner.GetExecutionBlockHash(_runner.JustifiedCheckpoint.Root),
            _runner.GetExecutionBlockHash(_runner.FinalizedCheckpoint.Root),
            _runner.JustifiedCheckpoint,
            _runner.FinalizedCheckpoint);
    }

    /// <inheritdoc/>
    public void OnInvalidExecutionPayload(Hash256 blockRoot, Hash256? latestValidHash) =>
        _runner.OnInvalidExecutionPayload(blockRoot, latestValidHash);

    /// <inheritdoc/>
    public void OnFinalized(CheckpointRef finalized)
    {
        ulong finalizedSlot = _runner.GetBlockSlot(finalized.Root) ?? BeaconStateAccessors.ComputeStartSlotAtEpoch(finalized.Epoch);

        BeaconStateFulu? finalizedState = _states.GetBlockState(finalized.Root);
        if (finalizedState is not null)
        {
            _store.PutState(finalized.Root, BeaconStateFulu.Encode(finalizedState));
            _store.SetAnchor(finalized.Root, finalizedSlot);
            // TryLoad requires an exact registry-length match against the anchor state, so only
            // persist when the cache has not yet been extended past the finalized registry.
            if (_pubkeys.Count == finalizedState.Validators!.Length)
            {
                _pubkeys.Persist(_store);
            }
        }
        else if (_logger.IsDebug)
        {
            _logger.Debug($"Finalized state {finalized.Root} is not retained; the persisted anchor stays at its previous checkpoint");
        }

        _runner.Prune();
        PruneStore(finalizedSlot);
    }

    /// <inheritdoc/>
    public void OnGossipAggregate(SignedAggregateAndProof aggregate)
    {
        try
        {
            _runner.OnAttestation(aggregate.Message!.Aggregate!, isFromBlock: false);
        }
        catch (Exception e) when (e is ForkChoiceException or BeaconStateException)
        {
            Metrics.BeaconChainForkChoiceRejections.Increment(GossipAggregateRejected);
            if (_logger.IsTrace) _logger.Trace($"Rejected gossip aggregate: {e.Message}");
        }
    }

    /// <inheritdoc/>
    public void OnGossipAttesterSlashing(AttesterSlashing slashing)
    {
        try
        {
            _runner.OnAttesterSlashing(slashing);
        }
        catch (Exception e) when (e is ForkChoiceException or BeaconStateException)
        {
            Metrics.BeaconChainForkChoiceRejections.Increment(GossipAttesterSlashingRejected);
            if (_logger.IsTrace) _logger.Trace($"Rejected gossip attester slashing: {e.Message}");
        }
    }

    /// <summary>
    /// The body replay <see cref="ForkChoiceRunner.OnBlock(SignedBeaconBlock, BeaconStateFulu, ExecutionStatus, IDataAvailabilityRule)"/>
    /// leaves to its caller: feeds the block's attestations and attester slashings (already verified
    /// by the transition) to fork choice. A refused operation is counted and skipped, never fatal.
    /// </summary>
    private void ApplyBodyOperations(BeaconBlockBody body)
    {
        foreach (Attestation attestation in body.Attestations!)
        {
            try
            {
                _runner.OnAttestation(attestation, isFromBlock: true, verifySignature: false);
            }
            catch (Exception e) when (e is ForkChoiceException or BeaconStateException)
            {
                // Body attestations may reference targets outside our block tree; that does not
                // invalidate the block (the transition already accepted it).
                Metrics.BeaconChainForkChoiceRejections.Increment(BodyAttestationRejected);
                if (_logger.IsTrace) _logger.Trace($"Skipped body attestation: {e.Message}");
            }
        }

        foreach (AttesterSlashing slashing in body.AttesterSlashings!)
        {
            try
            {
                _runner.OnAttesterSlashing(slashing, verifySignatures: false);
            }
            catch (Exception e) when (e is ForkChoiceException or BeaconStateException)
            {
                Metrics.BeaconChainForkChoiceRejections.Increment(BodyAttesterSlashingRejected);
                if (_logger.IsTrace) _logger.Trace($"Skipped body attester slashing: {e.Message}");
            }
        }
    }

    /// <summary>Moves the live lineage onto the new head after a reorg, with fresh per-lineage caches.</summary>
    private void AdoptHeadLineage(Hash256 head)
    {
        if (head == _states.LineageRoot)
        {
            return;
        }

        BeaconStateFulu? headState = _states.GetBlockState(head);
        if (headState is null)
        {
            // Imports building on this head fall back to the fork path until its state is seen again.
            if (_logger.IsWarn) _logger.Warn($"Reorg to {head} whose post-state is not retained; lineage stays at {_states.LineageRoot}");
            return;
        }

        if (_logger.IsInfo) _logger.Info($"Beacon chain reorg: adopting head {head} at slot {_runner.GetBlockSlot(head)} (was {_states.LineageRoot})");
        _states.Retain(_states.LineageRoot, _states.LineageState);
        _states.SetLineage(head, headState.Clone());
        _lineageCache = new EpochCache { Hasher = new CachedBeaconStateHasher() };
        // Unknown here; forces retention at the next epoch advance, which is harmless.
        _lineageBlockStartsEpoch = true;
    }

    /// <summary>Walks the new head's ancestry, rewriting the canonical slot index until it meets an already-canonical entry.</summary>
    private void UpdateCanonicalIndex(Hash256 head)
    {
        if (head == _canonicalHead)
        {
            return;
        }

        foreach (ProtoNode node in _runner.EnumerateAncestors(head))
        {
            if (_store.TryGetCanonicalRoot(node.Slot, out Hash256? existing) && existing == node.Root)
            {
                break;
            }

            _store.SetCanonicalRoot(node.Slot, node.Root);
        }

        _canonicalHead = head;
    }

    private void MaybeSnapshotState(Hash256 blockRoot, BeaconStateFulu state, ulong blockEpoch)
    {
        if (blockEpoch < _lastSnapshotEpoch + (ulong)_config.StateSnapshotIntervalEpochs)
        {
            return;
        }

        _store.PutState(blockRoot, BeaconStateFulu.Encode(state));
        _lastSnapshotEpoch = blockEpoch;
        if (_logger.IsDebug) _logger.Debug($"Persisted beacon state snapshot at epoch {blockEpoch} ({blockRoot})");
    }

    /// <summary>Deletes non-canonical blocks at or below the finalized slot and stops tracking the finalized range.</summary>
    private void PruneStore(ulong finalizedSlot)
    {
        List<Hash256>? finalizedRoots = null;
        int pruned = 0;
        foreach ((Hash256 root, ulong slot) in _unfinalized)
        {
            if (slot > finalizedSlot)
            {
                continue;
            }

            (finalizedRoots ??= []).Add(root);
            if (!_store.TryGetCanonicalRoot(slot, out Hash256? canonical) || canonical != root)
            {
                _store.DeleteBlock(root);
                pruned++;
            }
        }

        if (finalizedRoots is not null)
        {
            foreach (Hash256 root in finalizedRoots)
            {
                _unfinalized.Remove(root);
            }
        }

        if (pruned > 0 && _logger.IsDebug) _logger.Debug($"Pruned {pruned} non-canonical blocks below finalized slot {finalizedSlot}");
    }

    /// <summary>
    /// Carries one block's execution verdict from the state transition's <c>newPayload</c> hook
    /// back to the importer.
    /// </summary>
    /// <remarks>
    /// The verdict has to bind to the block that produced it. Reading it from a field on the
    /// shared <see cref="IEngineDriver"/> binds it instead to whichever caller ran
    /// <c>newPayload</c> last, which can admit a block to fork choice as
    /// <see cref="ExecutionStatus.Valid"/> when it was only <see cref="ExecutionStatus.Optimistic"/>.
    /// That is not recoverable: invalidating a node fork choice already holds as valid throws.
    /// One instance per import, so the binding holds by construction rather than by call ordering.
    /// </remarks>
    private sealed class ImportVerdict(IEngineDriver engine) : INewPayloadNotifier
    {
        private BeaconBlockBody? _primed;

        /// <summary>The verdict for this import, optimistic until the hook produces one.</summary>
        public ExecutionStatus Status { get; private set; } = ExecutionStatus.Optimistic;

        /// <summary>Obtains the verdict for <paramref name="body"/> ahead of the transition that will consume it.</summary>
        /// <remarks>The hook is served this verdict rather than calling the engine a second time.</remarks>
        /// <exception cref="EngineUnavailableException">The execution layer returned no verdict.</exception>
        public void Prime(BeaconBlockBody body)
        {
            Status = engine.NotifyNewPayload(body);
            _primed = body;
        }

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) =>
            ReferenceEquals(body, _primed) ? Status : Status = engine.NotifyNewPayload(body);

        public ExecutionStatus NotifyNewPayload(ExecutionPayloadGloas payload, Hash256?[] versionedHashes, Hash256 parentBeaconBlockRoot, ExecutionRequestsGloas executionRequests) =>
            Status = engine.NotifyNewPayload(payload, versionedHashes, parentBeaconBlockRoot, executionRequests);
    }
}

/// <inheritdoc cref="IBlockImporterFactory"/>
/// <remarks>
/// Every importer created here applies <see cref="CustodySamplingAvailability"/>: the production
/// <c>is_data_available</c>, fed from the gossip sidecar pool and this node's discovery identity. The
/// supernode <see cref="FullColumnSetAvailability"/> is for the consensus-spec vectors only and is
/// deliberately not reachable from this factory.
/// </remarks>
/// <param name="pool">Where an importer looks up the columns this node holds for a block.</param>
/// <param name="discovery">
/// Supplies the node id the custody columns derive from; <c>null</c> (the P2P-less configuration
/// some tests run) leaves the identity unknown, so no blob-carrying block is ever available.
/// </param>
/// <param name="clock">
/// The wall clock the <see cref="DataAvailabilityBoundary"/> is measured against; <c>null</c> (tests
/// that construct the factory by hand) means the system clock, which is what the container supplies.
/// </param>
/// <param name="forkChoiceSnapshots">Where every importer publishes its fork-choice snapshots; <c>null</c> publishes nothing.</param>
public sealed class BlockImporterFactory(
    BeaconChainSpec spec,
    BeaconChainStore store,
    PubkeyCache pubkeys,
    IEngineDriver engine,
    IBeaconChainConfig config,
    ILogManager logManager,
    DataColumnSidecarPool pool,
    BeaconDiscovery? discovery = null,
    SlotClock? clock = null,
    ForkChoiceSnapshotHolder? forkChoiceSnapshots = null) : IBlockImporterFactory
{
    private readonly SlotClock _clock = clock ?? new SlotClock(spec, Timestamper.Default);

    public IBlockImporter Create(BeaconStateFulu anchorState, SignedBeaconBlock anchorBlock, Hash256 anchorRoot) =>
        new BlockImporter(
            spec,
            store,
            pubkeys,
            engine,
            config,
            logManager,
            new CustodySamplingAvailability(new DiscoveryNodeCustodySource(discovery), new DataColumnPoolSource(pool), _clock),
            anchorState,
            anchorBlock,
            anchorRoot,
            forkChoiceSnapshots);

}
