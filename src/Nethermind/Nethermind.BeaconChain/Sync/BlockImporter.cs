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
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
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
/// Gloas blocks have no in-place lineage: each runs on a clone of its parent's post-state (a Fulu
/// parent is carried across the fork boundary on that clone), and the frozen result is retained for
/// fork choice and for verifying the block's execution payload envelope.
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

    /// <summary>Covers the orchestrator's retry set and gossip hold queue, 128 blocks each.</summary>
    private const int MaxDeferredBlocks = 256;

    private readonly BeaconChainSpec _spec;
    private readonly BeaconChainStore _store;
    private readonly PubkeyCache _pubkeys;
    private readonly IEngineDriver _engine;
    private readonly IBeaconChainConfig _config;
    private readonly SlotClock _clock;
    private readonly ILogger _logger;

    /// <summary>The <c>is_data_available</c> rule for blocks from the network; store replays use <see cref="ReplayedBlockAvailability"/>.</summary>
    private readonly IDataAvailabilityRule _availability;

    private readonly PostStateCache _states;
    private readonly ForkChoiceRunner _runner;
    private readonly ExecutionPayloadEnvelopeImporter _envelopes;
    private readonly ForkChoiceSnapshotHolder? _forkChoiceSnapshots;

    /// <summary>Imported-but-not-finalized block roots and their slots, for store pruning at finalization.</summary>
    private readonly Dictionary<Hash256, ulong> _unfinalized = [];

    /// <summary>
    /// Blocks last answered <see cref="BlockImportResult.ParentPayloadUnverified"/>, keyed by root, with the nearest ancestor fork
    /// choice holds, whose state checked their proposer; bounded by <see cref="MaxDeferredBlocks"/> and pruned at finalization.
    /// </summary>
    private readonly Dictionary<Hash256, DeferredBlock> _deferred = [];

    private EpochCache _lineageCache = new() { Hasher = new CachedBeaconStateHasher() };

    /// <summary>Whether the current lineage head block is the first block of its epoch (its post-state is then a checkpoint-root candidate).</summary>
    private bool _lineageBlockStartsEpoch = true;

    private Hash256 _canonicalHead;
    private ulong _canonicalIndexTopSlot;
    private ulong _lastSnapshotEpoch;

    /// <summary>The last Gloas block imported; only its children may reuse <see cref="_gloasLineageCache"/>, which refuses a memo built on another branch.</summary>
    private Hash256? _gloasLineageRoot;

    private EpochCache _gloasLineageCache = new();

    /// <summary>A Gloas anchor's root and its bid's <c>parent_block_hash</c>, which fork choice records only for blocks it imported; <c>null</c> for a Fulu anchor.</summary>
    private readonly Hash256? _gloasAnchorRoot;

    private readonly Hash256? _gloasAnchorParentBlockHash;

    private readonly record struct DeferredBlock(Hash256 AncestorRoot, ulong Slot);

    /// <param name="isEnvelopeDataAvailable">The Gloas <c>is_data_available</c> an execution payload envelope is checked against; see <see cref="ExecutionPayloadEnvelopeImporter"/>.</param>
    /// <param name="clock">The node's slot clock; a block after its current slot (within <c>MAXIMUM_GOSSIP_CLOCK_DISPARITY</c>) is refused.</param>
    /// <param name="anchorState">The post-state of <paramref name="anchorBlock"/>, of the same fork.</param>
    /// <param name="forkChoiceSnapshots">Where <see cref="ComputeHead"/> publishes a copy of the fork-choice store for readers off the import thread; <c>null</c> publishes nothing.</param>
    /// <exception cref="ArgumentException">The anchor state and block belong to different forks.</exception>
    public BlockImporter(
        BeaconChainSpec spec,
        BeaconChainStore store,
        PubkeyCache pubkeys,
        IEngineDriver engine,
        IBeaconChainConfig config,
        ILogManager logManager,
        IDataAvailabilityRule availability,
        Func<Hash256, ExecutionPayloadBid, bool> isEnvelopeDataAvailable,
        SlotClock clock,
        ForkedBeaconState anchorState,
        ForkedSignedBeaconBlock anchorBlock,
        Hash256 anchorRoot,
        ForkChoiceSnapshotHolder? forkChoiceSnapshots = null)
    {
        _spec = spec;
        _store = store;
        _pubkeys = pubkeys;
        _engine = engine;
        _config = config;
        _clock = clock;
        _logger = logManager.GetClassLogger<BlockImporter>();
        _availability = availability;
        _forkChoiceSnapshots = forkChoiceSnapshots;

        switch (anchorState, anchorBlock)
        {
            case (ForkedBeaconState.OfFulu { State: BeaconStateFulu fuluState }, ForkedSignedBeaconBlock.OfFulu { Block: SignedBeaconBlock fuluBlock }):
                _states = new PostStateCache(store, spec, anchorRoot, fuluState, IsGloasBlock, GetJustifiedRoot, logManager);
                _runner = new ForkChoiceRunner(spec, fuluState, fuluBlock.Message!, _states, pubkeys, _states);
                _lastSnapshotEpoch = fuluState.GetCurrentEpoch();
                break;
            case (ForkedBeaconState.OfGloas { State: BeaconStateGloas gloasState }, ForkedSignedBeaconBlock.OfGloas { Block: SignedBeaconBlockGloas gloasBlock }):
                // specs/gloas/fork-choice.md get_forkchoice_store: block_states holds the anchor state, the finalized checkpoint's.
                _states = new PostStateCache(store, spec, null, null, IsGloasBlock, GetJustifiedRoot, logManager);
                _states.PinGloas(anchorRoot, gloasState);
                _runner = new ForkChoiceRunner(spec, gloasState, gloasBlock.Message!, _states, pubkeys, _states);
                _gloasAnchorRoot = anchorRoot;
                _gloasAnchorParentBlockHash = gloasBlock.Message!.Body!.SignedExecutionPayloadBid!.Message!.ParentBlockHash;
                _lastSnapshotEpoch = gloasState.GetCurrentEpoch();
                break;
            default:
                throw new ArgumentException($"The anchor state at slot {anchorState.Slot} is a {anchorState.Fork} state, but its block at slot {anchorBlock.Slot} is a {anchorBlock.GetType().Name} block", nameof(anchorBlock));
        }

        _envelopes = new ExecutionPayloadEnvelopeImporter(_states, engine, pubkeys, isEnvelopeDataAvailable, logManager);
        _canonicalHead = anchorRoot;
        store.SetCanonicalRoot(anchorBlock.Slot, anchorRoot);
        // A restart must clear what the previous run indexed above the head it replays up to.
        _canonicalIndexTopSlot = Math.Max(anchorBlock.Slot, store.GetCanonicalIndexTopSlot() ?? 0);
    }

    /// <inheritdoc/>
    public bool IsKnown(Hash256 blockRoot) => _runner.ContainsBlock(blockRoot);

    private bool IsGloasBlock(Hash256 blockRoot) => _runner.GetBlockSlot(blockRoot) is ulong slot && SignedBeaconBlockCodec.IsGloasSlot(slot, _spec);

    private Hash256 GetJustifiedRoot() => _runner.JustifiedCheckpoint.Root;

    /// <inheritdoc/>
    public bool IsExpectedProposer(ForkedSignedBeaconBlock block)
    {
        if (block is ForkedSignedBeaconBlock.OfGloas)
        {
            // The lookahead of the parent's frozen post-state; a Fulu or unretained parent defers to the transition.
            return _states.GetGloasBlockState(block.ParentRoot) is not { } parentState
                || IsInLookahead(parentState.GetCurrentEpoch(), parentState.ProposerLookahead!, block.Slot, block.ProposerIndex);
        }

        // Without a Fulu lineage (a Gloas anchor) the transition, which refuses a Fulu block there, decides.
        if (_states.LineageState is not { } state)
        {
            return true;
        }

        ulong epoch = BeaconStateAccessors.ComputeEpochAtSlot(block.Slot);
        ulong stateEpoch = state.GetCurrentEpoch();
        if (epoch != stateEpoch && epoch != stateEpoch + 1)
        {
            return true;
        }

        return state.GetBeaconProposerIndex(block.Slot) == block.ProposerIndex;
    }

    /// <summary>Whether an EIP-7917 lookahead taken at <paramref name="stateEpoch"/> names <paramref name="proposerIndex"/> for <paramref name="slot"/>; <c>true</c> outside its two-epoch window.</summary>
    private static bool IsInLookahead(ulong stateEpoch, ulong[] lookahead, ulong slot, ulong proposerIndex)
    {
        ulong epoch = BeaconStateAccessors.ComputeEpochAtSlot(slot);
        if (epoch != stateEpoch && epoch != stateEpoch + 1)
        {
            return true;
        }

        int offset = epoch == stateEpoch ? 0 : (int)Presets.SlotsPerEpoch;
        return lookahead[offset + (int)(slot % Presets.SlotsPerEpoch)] == proposerIndex;
    }

    /// <inheritdoc/>
    public BlockImportResult Import(ForkedSignedBeaconBlock block, Hash256 blockRoot, bool verifySignatures)
    {
        // Recorded again only if this attempt defers it too.
        if (_deferred.Count > 0)
        {
            _deferred.Remove(blockRoot);
        }

        if (_runner.ContainsBlock(blockRoot))
        {
            return BlockImportResult.AlreadyKnown;
        }

        long receivedMs = _clock.UnixMilliseconds;

        // Refused before any engine call or state copy: fork choice would refuse the shape only after the transition ran.
        if (SignedBeaconBlockCodec.IsGloasSlot(block.Slot, _spec) != block is ForkedSignedBeaconBlock.OfGloas)
        {
            if (_logger.IsWarn) _logger.Warn($"Dropping block {blockRoot} at slot {block.Slot}: a {block.GetType().Name} block does not belong to the fork of its slot");
            return BlockImportResult.Invalid;
        }

        if (!_runner.ContainsBlock(block.ParentRoot))
        {
            return verifySignatures && block is ForkedSignedBeaconBlock.OfGloas gloasChild && _deferred.TryGetValue(block.ParentRoot, out DeferredBlock deferredParent)
                ? DeferBehindDeferredParent(gloasChild.Block, blockRoot, deferredParent)
                : BlockImportResult.UnknownParent;
        }

        if (CheckBeforeTransition(block.Slot, block.ParentRoot) is { } refusal)
        {
            if (_logger.IsWarn) _logger.Warn($"Dropping block {blockRoot} at slot {block.Slot} before its state transition: {refusal}");
            return BlockImportResult.Invalid;
        }

        return block switch
        {
            ForkedSignedBeaconBlock.OfFulu fulu => ImportFulu(fulu.Block, blockRoot, verifySignatures, receivedMs),
            ForkedSignedBeaconBlock.OfGloas gloas => ImportGloas(gloas, blockRoot, verifySignatures, receivedMs),
            _ => throw new NotSupportedException($"Unhandled block {block.GetType().Name}"),
        };
    }

    /// <summary>
    /// The <c>on_block</c> assertions that precede <c>state_transition</c> (specs/phase0/fork-choice.md), in spec
    /// order, then the transition's own <c>block.slot &gt; parent slot</c>: a block that fails any of them costs no
    /// <c>process_slots</c>, which is linear in the slot distance to the parent.
    /// </summary>
    /// <returns>Why the block is refused, or <c>null</c>.</returns>
    /// <remarks>
    /// The current slot is the node's clock, allowing <c>MAXIMUM_GOSSIP_CLOCK_DISPARITY</c> as gossip does, never
    /// fork-choice time: <see cref="TickToClock"/> advances that to at least the block's own slot before <c>OnBlock</c>.
    /// </remarks>
    private string? CheckBeforeTransition(ulong slot, Hash256 parentRoot)
    {
        ulong currentSlot = _clock.CurrentSlot;
        ulong latestSlot = _clock.UnixMilliseconds + GossipRouter.MaximumGossipClockDisparityMs >= _clock.SlotStartMilliseconds(currentSlot + 1) ? currentSlot + 1 : currentSlot;
        if (slot > latestSlot)
        {
            return $"the block is from the future (current slot {currentSlot})";
        }

        CheckpointRef finalized = _runner.FinalizedCheckpoint;
        ulong finalizedSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(finalized.Epoch);
        if (slot <= finalizedSlot)
        {
            return $"the block is not after the finalized slot {finalizedSlot}";
        }

        // The spec's get_checkpoint_block(parent_root, finalized.epoch) == finalized.root.
        Hash256? checkpointBlock = null;
        foreach (ProtoNode node in _runner.EnumerateAncestors(parentRoot))
        {
            // specs/bellatrix/optimistic-sync.md: the parent of the block MUST NOT have an INVALIDATED execution payload.
            if (node.Root == parentRoot && node.ExecutionStatus == ExecutionStatus.Invalid)
            {
                return $"its parent {parentRoot} has an invalid execution payload";
            }

            if (node.Slot <= finalizedSlot)
            {
                checkpointBlock = node.Root;
                break;
            }
        }

        if (checkpointBlock != finalized.Root)
        {
            return $"the block does not descend from the finalized checkpoint {finalized}";
        }

        ulong parentSlot = _runner.GetBlockSlot(parentRoot)!.Value;
        return slot > parentSlot ? null : $"the block is not after its parent's slot {parentSlot}";
    }

    private BlockImportResult ImportFulu(SignedBeaconBlock signedBlock, Hash256 blockRoot, bool verifySignatures, long receivedMs)
    {
        BeaconBlock block = signedBlock.Message!;
        Hash256 parentRoot = block.ParentRoot!;

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
        TickToClock(block.Slot, receivedMs);

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

    /// <summary>The Gloas <c>on_block</c> with its state transition; every fallible step runs before anything is stored.</summary>
    private BlockImportResult ImportGloas(ForkedSignedBeaconBlock.OfGloas forked, Hash256 blockRoot, bool verifySignatures, long receivedMs)
    {
        SignedBeaconBlockGloas signedBlock = forked.Block;
        BeaconBlockGloas block = signedBlock.Message!;
        Hash256 parentRoot = block.ParentRoot!;

        // specs/gloas/fork-choice.md on_block: if is_parent_node_full, assert is_payload_verified(parent_root), before state_transition.
        bool parentPayloadUnverified = _runner.IsParentNodeFull(block) && !_runner.IsPayloadVerified(parentRoot);
        if (parentPayloadUnverified && verifySignatures)
        {
            return DeferForParentPayload(signedBlock, blockRoot, parentRoot, parentDeferred: false);
        }

        ulong parentSlot = _runner.GetBlockSlot(parentRoot)!.Value;
        BeaconStateGloas? gloasParent = null;
        ForkedBeaconState? parentState;
        if (SignedBeaconBlockCodec.IsGloasSlot(parentSlot, _spec))
        {
            gloasParent = _states.GetGloasBlockState(parentRoot);
            parentState = gloasParent is null ? null : new ForkedBeaconState.OfGloas(gloasParent.Clone());
        }
        else
        {
            parentState = _states.CopyBlockState(parentRoot) is { } fuluParent ? new ForkedBeaconState.OfFulu(fuluParent) : null;
        }

        if (parentState is null)
        {
            if (_logger.IsWarn) _logger.Warn($"Cannot import block at slot {block.Slot}: the post-state of its parent {parentRoot} is no longer retained");
            return BlockImportResult.UnknownParent;
        }

        EpochCache cache = parentRoot == _gloasLineageRoot ? _gloasLineageCache : new EpochCache();
        BeaconStateGloas postState;
        try
        {
            // On a copy: the transition mutates before its last check, and UpgradeToGloas aliases the Fulu parent's arrays.
            postState = ((ForkedBeaconState.OfGloas)ForkedStateTransition.Apply(parentState, forked, cache, _pubkeys, new ImportVerdict(_engine), _spec, validateResult: true, verifySignatures)).State;
        }
        catch (BeaconStateException e)
        {
            if (_logger.IsWarn) _logger.Warn($"Dropping invalid block {blockRoot} at slot {block.Slot}: {e.Message}");
            return BlockImportResult.Invalid;
        }

        if (parentPayloadUnverified)
        {
            // Recorded before OnBlock, which needs it: a stored full child was persisted only after passing that gate, so the record holds even if OnBlock now refuses it.
            _runner.OnExecutionPayloadVerified(parentRoot);
        }

        TickToClock(block.Slot, receivedMs);
        try
        {
            _runner.OnBlock(signedBlock, postState);
        }
        catch (Exception e) when (e is ForkChoiceException or BeaconStateException)
        {
            if (_logger.IsWarn) _logger.Warn($"Dropping block {blockRoot} at slot {block.Slot} rejected by fork choice: {e.Message}");
            return BlockImportResult.Invalid;
        }

        // Only after OnBlock: the Gloas tier must hold states of blocks fork choice accepted (the spec's store.block_states).
        bool startsEpoch = block.Slot % _spec.SlotsPerEpoch == 0;
        _states.RetainGloas(blockRoot, postState, checkpointCandidate: startsEpoch);
        if (gloasParent is not null && _spec.GetEpoch(block.Slot) > _spec.GetEpoch(parentSlot) + (startsEpoch ? 1UL : 0UL))
        {
            // The parent is the checkpoint block of every epoch whose start slot this block skipped.
            _states.RetainGloas(parentRoot, gloasParent, checkpointCandidate: true);
        }

        ApplyBodyOperations(block.Body!);

        _store.PutForkedBlock(blockRoot, forked);
        _unfinalized[blockRoot] = block.Slot;
        _gloasLineageRoot = blockRoot;
        _gloasLineageCache = cache;
        return BlockImportResult.Imported;
    }

    /// <summary>
    /// Answers <see cref="BlockImportResult.ParentPayloadUnverified"/> for a block whose full parent's payload is not
    /// verified, or whose parent is itself deferred, once its proposer and proposer signature check out against the
    /// post-state of <paramref name="ancestorRoot"/>, the nearest ancestor fork choice holds.
    /// </summary>
    /// <remarks>
    /// A deferred block waits in a bounded queue and is marked seen for its (slot, proposer), so an unsigned
    /// one must be refused here or it could crowd out the real block. The proposer domain is the one the block's
    /// own pre-state has: its fork version is fixed for every epoch from the ancestor's on. Past the ancestor's
    /// lookahead window the proposer of a child of the ancestor is read from a copy of the ancestor's post-state advanced
    /// by <c>process_slots</c>, the pre-state the block's own transition starts from; the clock check has already bounded
    /// that distance. A block with deferred blocks in between is not deferred there: their RANDAO reveals feed the seed
    /// of that lookahead, and their states are not held.
    /// </remarks>
    private BlockImportResult DeferForParentPayload(SignedBeaconBlockGloas signedBlock, Hash256 blockRoot, Hash256 ancestorRoot, bool parentDeferred)
    {
        BeaconBlockGloas block = signedBlock.Message!;
        // A full parent whose payload is unverified is a Gloas block: a known Fulu block counts as verified.
        if (_states.GetGloasBlockState(ancestorRoot) is not { } ancestorState)
        {
            if (_logger.IsWarn) _logger.Warn($"Cannot import block at slot {block.Slot}: the post-state of its ancestor {ancestorRoot} is no longer retained");
            return BlockImportResult.UnknownParent;
        }

        string? refusal;
        try
        {
            BeaconStateGloas proposerState = ancestorState;
            if (BeaconStateAccessors.ComputeEpochAtSlot(block.Slot) > ancestorState.GetCurrentEpoch() + 1)
            {
                if (parentDeferred)
                {
                    if (_logger.IsDebug) _logger.Debug($"Not deferring block {blockRoot} at slot {block.Slot}: it is past the proposer lookahead of its ancestor {ancestorRoot}");
                    return BlockImportResult.UnknownParent;
                }

                proposerState = ancestorState.Clone();
                GloasSlotProcessing.ProcessSlots(proposerState, block.Slot);
            }

            refusal = CheckProposal(signedBlock, proposerState);
        }
        catch (BeaconStateException e)
        {
            refusal = e.Message;
        }

        if (refusal is not null)
        {
            if (_logger.IsWarn) _logger.Warn($"Dropping invalid block {blockRoot} at slot {block.Slot}: {refusal}");
            return BlockImportResult.Invalid;
        }

        if (_deferred.Count < MaxDeferredBlocks)
        {
            _deferred[blockRoot] = new DeferredBlock(ancestorRoot, block.Slot);
        }

        if (_logger.IsDebug) _logger.Debug($"Deferring block {blockRoot} at slot {block.Slot}: a payload it builds on is not verified");
        return BlockImportResult.ParentPayloadUnverified;
    }

    /// <summary>The deferral of a Gloas block whose parent is deferred too, after the <c>on_block</c> checks that precede its transition.</summary>
    private BlockImportResult DeferBehindDeferredParent(SignedBeaconBlockGloas signedBlock, Hash256 blockRoot, DeferredBlock parent)
    {
        ulong slot = signedBlock.Message!.Slot;
        string? refusal = CheckBeforeTransition(slot, parent.AncestorRoot) ?? (slot > parent.Slot ? null : $"the block is not after its parent's slot {parent.Slot}");
        if (refusal is not null)
        {
            if (_logger.IsWarn) _logger.Warn($"Dropping block {blockRoot} at slot {slot} before its state transition: {refusal}");
            return BlockImportResult.Invalid;
        }

        return DeferForParentPayload(signedBlock, blockRoot, parent.AncestorRoot, parentDeferred: true);
    }

    /// <summary>Checks the block's proposer against the lookahead of <paramref name="state"/> and its proposer signature under that state's domain.</summary>
    /// <returns>Why the proposal is refused, or <c>null</c>.</returns>
    private string? CheckProposal(SignedBeaconBlockGloas signedBlock, BeaconStateGloas state)
    {
        BeaconBlockGloas block = signedBlock.Message!;
        try
        {
            if (!IsInLookahead(state.GetCurrentEpoch(), state.ProposerLookahead!, block.Slot, block.ProposerIndex))
            {
                return $"proposer {block.ProposerIndex} is not the expected proposer";
            }

            return GloasBlockProcessing.VerifyProposerSignature(state, signedBlock, _pubkeys) ? null : "invalid proposer signature";
        }
        catch (BeaconStateException e)
        {
            return e.Message;
        }
    }

    /// <inheritdoc/>
    public ExecutionPayloadEnvelopeImportResult ImportEnvelope(SignedExecutionPayloadEnvelope envelope)
    {
        Hash256? blockRoot = envelope.Message!.BeaconBlockRoot;
        if (blockRoot is not null)
        {
            if (_runner.GetBlockSlot(blockRoot) is not ulong slot)
            {
                // The Gloas tier can outlive a root fork choice pruned; the spec asserts the root is in store.block_states.
                return ExecutionPayloadEnvelopeImportResult.UnknownBlock;
            }

            if (SignedBeaconBlockCodec.IsGloasSlot(slot, _spec) && _runner.IsPayloadVerified(blockRoot))
            {
                return ExecutionPayloadEnvelopeImportResult.AlreadyKnown;
            }
        }

        ExecutionPayloadEnvelopeImportResult result = _envelopes.Import(envelope);
        if (result is ExecutionPayloadEnvelopeImportResult.Valid or ExecutionPayloadEnvelopeImportResult.Optimistic)
        {
            // An optimistic payload is recorded as an optimistic block is imported; neither verdict promotes the execution status.
            _runner.OnExecutionPayloadVerified(blockRoot!);
        }

        return result;
    }

    /// <summary>Advances fork-choice time to the node's clock when the block reached the importer, before <c>on_block</c>, whose proposer boost and <c>record_block_timeliness</c> read <c>store.time</c>.</summary>
    /// <remarks>
    /// The spec's <c>on_block</c> runs at the <c>store.time</c> of the last tick before it, so the node's own state
    /// transition and <c>engine_newPayload</c> latency do not count as block lateness. Never below
    /// <paramref name="blockSlot"/>'s start: a block up to <c>MAXIMUM_GOSSIP_CLOCK_DISPARITY</c> early must still pass the current-slot check.
    /// </remarks>
    private void TickToClock(ulong blockSlot, long receivedMs)
    {
        ulong now = _runner.GenesisTime + (ulong)Math.Max(0L, receivedMs - _clock.SlotStartMilliseconds(0)) / 1000;
        ulong time = Math.Max(now, _runner.GenesisTime + blockSlot * _spec.SecondsPerSlot);
        if (time > _runner.Time)
        {
            _runner.OnTick(time);
        }
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
            GetParentBlockHash(head) is { } headParentHash && !_runner.IsPayloadVerified(head) ? headParentHash : _runner.GetExecutionBlockHash(head),
            CheckpointExecutionHash(_runner.JustifiedCheckpoint.Root),
            CheckpointExecutionHash(_runner.FinalizedCheckpoint.Root),
            _runner.JustifiedCheckpoint,
            _runner.FinalizedCheckpoint);
    }

    /// <summary>specs/gloas/fork-choice.md <c>notify_forkchoice_updated</c>: a Gloas checkpoint block maps to its bid's <c>parent_block_hash</c>.</summary>
    private Hash256? CheckpointExecutionHash(Hash256 root) => GetParentBlockHash(root) ?? _runner.GetExecutionBlockHash(root);

    /// <summary>The bid's <c>parent_block_hash</c> of a Gloas block fork choice holds, a Gloas anchor's included; <c>null</c> for a Fulu block.</summary>
    private Hash256? GetParentBlockHash(Hash256 root) => _runner.GetParentBlockHash(root) ?? (root == _gloasAnchorRoot ? _gloasAnchorParentBlockHash : null);

    /// <inheritdoc/>
    public void OnInvalidExecutionPayload(Hash256 blockRoot, Hash256? latestValidHash) =>
        _runner.OnInvalidExecutionPayload(blockRoot, latestValidHash);

    /// <inheritdoc/>
    public void OnFinalized(CheckpointRef finalized)
    {
        ulong finalizedSlot = _runner.GetBlockSlot(finalized.Root) ?? BeaconStateAccessors.ComputeStartSlotAtEpoch(finalized.Epoch);

        // Chosen by slot, so a Fulu root never costs the Gloas getter a store read.
        bool gloasFinalized = SignedBeaconBlockCodec.IsGloasSlot(finalizedSlot, _spec);
        if (gloasFinalized && _states.GetGloasBlockState(finalized.Root) is { } gloasState)
        {
            PersistAnchor(finalized.Root, finalizedSlot, BeaconStateGloas.Encode(gloasState), gloasState.Validators!.Length);
            _states.PinGloas(finalized.Root, gloasState);
        }
        else if (!gloasFinalized && _states.GetBlockState(finalized.Root) is { } finalizedState)
        {
            PersistAnchor(finalized.Root, finalizedSlot, BeaconStateFulu.Encode(finalizedState), finalizedState.Validators!.Length);
        }
        else if (_logger.IsDebug)
        {
            _logger.Debug($"Finalized state {finalized.Root} is not retained; the persisted anchor stays at its previous checkpoint");
        }

        _runner.Prune();
        PruneStore(finalizedSlot);
        // on_block refuses every block at or below the finalized epoch's start slot, even when that slot is empty.
        ulong neverImportable = Math.Max(finalizedSlot, BeaconStateAccessors.ComputeStartSlotAtEpoch(finalized.Epoch));
        foreach ((Hash256 root, DeferredBlock deferred) in _deferred)
        {
            if (deferred.Slot <= neverImportable)
            {
                _deferred.Remove(root);
            }
        }
    }

    private void PersistAnchor(Hash256 root, ulong slot, byte[] stateSsz, int validatorCount)
    {
        _store.PutState(root, stateSsz);
        _store.SetAnchor(root, slot);
        // TryLoad requires an exact registry-length match against the anchor state, so only
        // persist when the cache has not yet been extended past the finalized registry.
        if (_pubkeys.Count == validatorCount)
        {
            _pubkeys.Persist(_store);
        }
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
    public void OnGossipAggregate(SignedAggregateAndProofGloas aggregate)
    {
        try
        {
            _runner.OnAttestation(aggregate.Message!.Aggregate!, isFromBlock: false);
        }
        catch (Exception e) when (e is ForkChoiceException or BeaconStateException)
        {
            Metrics.BeaconChainForkChoiceRejections.Increment(GossipAggregateRejected);
            if (_logger.IsTrace) _logger.Trace($"Rejected Gloas gossip aggregate: {e.Message}");
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

    /// <inheritdoc/>
    public void OnGossipAttesterSlashing(AttesterSlashingGloas slashing)
    {
        try
        {
            _runner.OnAttesterSlashing(slashing);
        }
        catch (Exception e) when (e is ForkChoiceException or BeaconStateException)
        {
            Metrics.BeaconChainForkChoiceRejections.Increment(GossipAttesterSlashingRejected);
            if (_logger.IsTrace) _logger.Trace($"Rejected Gloas gossip attester slashing: {e.Message}");
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

    /// <summary>The Gloas body replay, under the Fulu replay's policy.</summary>
    /// <remarks>Payload attestations are not fed: fork choice keeps no payload timeliness votes yet (the spec's <c>notify_ptc_messages</c>).</remarks>
    private void ApplyBodyOperations(BeaconBlockBodyGloas body)
    {
        foreach (AttestationGloas attestation in body.Attestations!)
        {
            try
            {
                _runner.OnAttestation(attestation, isFromBlock: true, verifySignature: false);
            }
            catch (Exception e) when (e is ForkChoiceException or BeaconStateException)
            {
                Metrics.BeaconChainForkChoiceRejections.Increment(BodyAttestationRejected);
                if (_logger.IsTrace) _logger.Trace($"Skipped body attestation: {e.Message}");
            }
        }

        foreach (AttesterSlashingGloas slashing in body.AttesterSlashings!)
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
    /// <remarks>A Gloas head has no Fulu lineage to adopt; the Fulu lineage stays on the last Fulu block it followed.</remarks>
    private void AdoptHeadLineage(Hash256 head)
    {
        if (head == _states.LineageRoot || _runner.GetBlockSlot(head) is ulong headSlot && SignedBeaconBlockCodec.IsGloasSlot(headSlot, _spec))
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
        _states.Retain(_states.LineageRoot!, _states.LineageState!);
        _states.SetLineage(head, headState.Clone());
        _lineageCache = new EpochCache { Hasher = new CachedBeaconStateHasher() };
        // Unknown here; forces retention at the next epoch advance, which is harmless.
        _lineageBlockStartsEpoch = true;
    }

    /// <summary>Makes the canonical slot index name exactly the new head's ancestry: each ancestor at its slot, and no entry at a slot the chain skips or above its head.</summary>
    /// <remarks>
    /// The walk stops below the first already-canonical ancestor whose empty slots under it hold no entry. So one head change
    /// costs one write per block and per stale entry above the common ancestor, one read per slot from the common ancestor's
    /// parent up to the higher of the new head and the index's recorded top slot, and never walks past the fork-choice root.
    /// Every change is computed from the index as it was and applied in one batch, so an interrupted update leaves the
    /// previous index whole and the next head change starts from it.
    /// </remarks>
    private void UpdateCanonicalIndex(Hash256 head)
    {
        if (head == _canonicalHead)
        {
            return;
        }

        List<(ulong Slot, Hash256? Root)> changes = [];
        ulong? headSlot = null;
        ulong? childSlot = null;
        bool childWasCanonical = false;
        foreach (ProtoNode node in _runner.EnumerateAncestors(head))
        {
            headSlot ??= node.Slot;
            // Above the head, clear up to the index's top slot; below it, the slots between this block and its child.
            ulong clearTo = childSlot is ulong child ? child - 1 : Math.Max(_canonicalIndexTopSlot, node.Slot);
            bool skippedSlotsWereEmpty = true;
            for (ulong slot = node.Slot + 1; slot <= clearTo; slot++)
            {
                if (_store.TryGetCanonicalRoot(slot, out _))
                {
                    changes.Add((slot, null));
                    skippedSlotsWereEmpty = false;
                }
            }

            if (childWasCanonical && skippedSlotsWereEmpty)
            {
                break;
            }

            childWasCanonical = _store.TryGetCanonicalRoot(node.Slot, out Hash256? existing) && existing == node.Root;
            if (!childWasCanonical)
            {
                changes.Add((node.Slot, node.Root));
            }

            childSlot = node.Slot;
        }

        ulong topSlot = headSlot ?? _canonicalIndexTopSlot;
        _store.ApplyCanonicalIndexChanges(changes, topSlot);
        _canonicalHead = head;
        _canonicalIndexTopSlot = topSlot;
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
/// Every importer created here applies <see cref="CustodySamplingAvailability"/> to Fulu blocks and
/// <see cref="GloasCustodySamplingAvailability"/> to Gloas execution payload envelopes: the production
/// <c>is_data_available</c>, fed from the gossip sidecar pool and this node's discovery identity. The
/// supernode <see cref="FullColumnSetAvailability"/> is for the consensus-spec vectors only and is
/// deliberately not reachable from this factory.
/// </remarks>
/// <param name="pool">Where an importer looks up the columns this node holds for a block.</param>
/// <param name="clock">
/// The one wall clock the <see cref="DataAvailabilityBoundary"/> and the future-slot bound are measured against, shared with
/// range sync so both agree on the window.
/// </param>
/// <param name="discovery">
/// Supplies the node id the custody columns derive from; <c>null</c> (the P2P-less configuration
/// some tests run) leaves the identity unknown, so no blob-carrying block is ever available.
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
    SlotClock clock,
    BeaconDiscovery? discovery = null,
    ForkChoiceSnapshotHolder? forkChoiceSnapshots = null) : IBlockImporterFactory
{
    public IBlockImporter Create(ForkedBeaconState anchorState, ForkedSignedBeaconBlock anchorBlock, Hash256 anchorRoot)
    {
        DiscoveryNodeCustodySource custody = new(discovery);
        return new BlockImporter(
            spec,
            store,
            pubkeys,
            engine,
            config,
            logManager,
            new CustodySamplingAvailability(custody, new DataColumnPoolSource(pool), clock),
            new GloasCustodySamplingAvailability(custody, pool, clock, spec).IsDataAvailable,
            clock,
            anchorState,
            anchorBlock,
            anchorRoot,
            forkChoiceSnapshots);
    }

}
