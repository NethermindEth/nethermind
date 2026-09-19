// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>
/// The spec-level fork-choice handlers (<c>on_tick</c>, <c>on_block</c>, <c>on_attestation</c>,
/// <c>on_attester_slashing</c>, <c>get_head</c>) over the proto-array implementation, following
/// the consensus-specs fork-choice document (Electra/Fulu rules).
/// </summary>
/// <remarks>
/// Owns the spec <c>Store</c> state that is not in the proto-array: wall-clock time, the realized
/// and unrealized checkpoints (via <see cref="ForkChoiceStore"/>), the proposer boost root,
/// equivocating indices, queued current-slot attestations, and the derived checkpoint states and
/// justified balances (cached per checkpoint). Block post-states come from
/// <see cref="IForkChoiceStateProvider"/>; the state transition itself stays outside — callers run
/// it and pass the post-state to <see cref="OnBlock"/>. Unrealized checkpoints are computed
/// clone-free via <see cref="EpochProcessing.ComputeJustificationAndFinalization"/>.
/// Not thread-safe.
/// </remarks>
public sealed class ForkChoiceRunner
{
    private readonly BeaconChainSpec _spec;
    private readonly IForkChoiceStateProvider _stateProvider;
    private readonly PubkeyCache _pubkeys;
    private readonly ForkChoiceStore _store;
    private readonly ProtoArrayForkChoice _protoArray;
    private readonly HashSet<ulong> _equivocatingIndices = [];
    private readonly List<QueuedAttestation> _queuedAttestations = [];
    private readonly Dictionary<CheckpointRef, BeaconStateFulu> _checkpointStates = [];
    private readonly Dictionary<CheckpointRef, JustifiedBalances> _justifiedBalances = [];

    /// <summary>Committee shufflings only; safe to share across forks (keyed by decision root). The balance memo is never used through this instance.</summary>
    private readonly EpochCache _committees = new();

    /// <summary>An attestation for the current slot, validated and indexed, waiting for the next slot tick (the spec only counts attestations from past slots).</summary>
    private readonly record struct QueuedAttestation(ulong Slot, ulong[] AttestingIndices, Hash256 BlockRoot, ulong TargetEpoch);

    /// <summary>Creates the store from an anchor (the spec's <c>get_forkchoice_store</c>): the anchor becomes the justified and finalized checkpoint at its own epoch.</summary>
    /// <param name="anchorState">The post-state of <paramref name="anchorBlock"/>; also supplies the genesis time.</param>
    /// <param name="anchorBlock">The finalized block to root the block tree at.</param>
    /// <exception cref="ForkChoiceException">The anchor block's state root does not match the anchor state.</exception>
    public ForkChoiceRunner(
        BeaconChainSpec spec,
        BeaconStateFulu anchorState,
        BeaconBlock anchorBlock,
        IForkChoiceStateProvider stateProvider,
        PubkeyCache pubkeys)
    {
        if (anchorBlock.StateRoot != SszRoots.HashTreeRoot(anchorState))
            throw new ForkChoiceException("Anchor block state root does not match the anchor state");

        _spec = spec;
        _stateProvider = stateProvider;
        _pubkeys = pubkeys;
        GenesisTime = anchorState.GenesisTime;
        Time = GenesisTime + spec.SecondsPerSlot * anchorState.Slot;

        Hash256 anchorRoot = SszRoots.HashTreeRoot(anchorBlock);
        CheckpointRef anchorCheckpoint = new(anchorState.GetCurrentEpoch(), anchorRoot);
        Hash256? payloadHash = anchorBlock.Body?.ExecutionPayload?.BlockHash;
        _store = new ForkChoiceStore(spec.SlotsPerEpoch, anchorState.Slot, anchorCheckpoint, anchorCheckpoint);
        _protoArray = new ProtoArrayForkChoice(
            currentSlot: anchorState.Slot,
            finalizedBlockSlot: anchorBlock.Slot,
            finalizedBlockStateRoot: anchorBlock.StateRoot!,
            justifiedCheckpoint: anchorCheckpoint,
            finalizedCheckpoint: anchorCheckpoint,
            executionStatus: payloadHash is null ? ExecutionStatus.Irrelevant : ExecutionStatus.Valid,
            executionBlockHash: payloadHash,
            slotsPerEpoch: spec.SlotsPerEpoch);
    }

    /// <summary>The wall-clock time in seconds (the spec store's <c>time</c>).</summary>
    public ulong Time { get; private set; }

    public ulong GenesisTime { get; }

    public ulong CurrentSlot => _store.CurrentSlot;

    public CheckpointRef JustifiedCheckpoint => _store.JustifiedCheckpoint;

    public CheckpointRef FinalizedCheckpoint => _store.FinalizedCheckpoint;

    public Hash256 ProposerBoostRoot => _store.ProposerBoostRoot;

    public bool ContainsBlock(Hash256 blockRoot) => _protoArray.ContainsBlock(blockRoot);

    /// <inheritdoc cref="ProtoArrayForkChoice.GetBlockSlot"/>
    public ulong? GetBlockSlot(Hash256 blockRoot) => _protoArray.GetBlockSlot(blockRoot);

    /// <inheritdoc cref="ProtoArrayForkChoice.GetExecutionBlockHash"/>
    public Hash256? GetExecutionBlockHash(Hash256 blockRoot) => _protoArray.GetExecutionBlockHash(blockRoot);

    /// <inheritdoc cref="ProtoArrayForkChoice.EnumerateAncestorNodes"/>
    public IEnumerable<ProtoNode> EnumerateAncestors(Hash256 blockRoot) => _protoArray.EnumerateAncestorNodes(blockRoot);

    /// <summary>
    /// Prunes fork-choice state below the finalized checkpoint: the proto-array block tree (subject
    /// to its prune threshold) and the cached checkpoint states and justified balances of epochs
    /// before the finalized one.
    /// </summary>
    public void Prune()
    {
        CheckpointRef finalized = _store.FinalizedCheckpoint;
        _protoArray.MaybePrune(finalized.Root);
        PruneCheckpointCache(_checkpointStates, finalized.Epoch);
        PruneCheckpointCache(_justifiedBalances, finalized.Epoch);
    }

    private static void PruneCheckpointCache<TValue>(Dictionary<CheckpointRef, TValue> cache, ulong finalizedEpoch)
    {
        List<CheckpointRef>? stale = null;
        foreach (CheckpointRef checkpoint in cache.Keys)
        {
            if (checkpoint.Epoch < finalizedEpoch) (stale ??= []).Add(checkpoint);
        }

        if (stale is not null)
        {
            foreach (CheckpointRef checkpoint in stale) cache.Remove(checkpoint);
        }
    }

    /// <summary>
    /// The spec's <c>on_tick</c>: advances the store to <paramref name="time"/> (seconds), resetting
    /// the proposer boost and pulling up unrealized checkpoints at every slot/epoch boundary
    /// crossed, then applies attestations queued for slots that are now in the past.
    /// </summary>
    /// <exception cref="ForkChoiceException">Time moved backwards.</exception>
    public void OnTick(ulong time)
    {
        if (time < Time)
            throw new ForkChoiceException($"Cannot move the store time backwards from {Time} to {time}");

        Time = time;
        _store.OnTick((time - GenesisTime) / _spec.SecondsPerSlot);
        DequeueAttestations();
    }

    /// <summary>
    /// The spec's <c>on_block</c>, minus the state transition: the caller has already computed
    /// <paramref name="postState"/> by applying <paramref name="signedBlock"/> to its parent state.
    /// Validates the block against the store, applies the proposer boost when timely, updates the
    /// realized and unrealized checkpoints, and registers the block with the proto-array.
    /// </summary>
    /// <param name="executionStatus">The execution layer's verdict on the block's payload, usually <see cref="ExecutionStatus.Optimistic"/> until <c>newPayload</c> completes.</param>
    /// <exception cref="ForkChoiceException">The block violates an <c>on_block</c> assertion.</exception>
    public void OnBlock(SignedBeaconBlock signedBlock, BeaconStateFulu postState, ExecutionStatus executionStatus = ExecutionStatus.Optimistic)
    {
        BeaconBlock block = signedBlock.Message!;
        Hash256 parentRoot = block.ParentRoot!;
        CheckpointRef finalized = _store.FinalizedCheckpoint;
        if (!_protoArray.ContainsBlock(parentRoot))
            throw new ForkChoiceException($"Parent {parentRoot} of the block at slot {block.Slot} is unknown to fork choice");
        if (block.Slot > _store.CurrentSlot)
            throw new ForkChoiceException($"Block at slot {block.Slot} is from the future (current slot {_store.CurrentSlot})");
        ulong finalizedSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(finalized.Epoch);
        if (block.Slot <= finalizedSlot)
            throw new ForkChoiceException($"Block at slot {block.Slot} is not after the finalized slot {finalizedSlot}");
        if (GetCheckpointBlock(parentRoot, finalized.Epoch) != finalized.Root)
            throw new ForkChoiceException($"Block at slot {block.Slot} does not descend from the finalized checkpoint {finalized}");

        Hash256 blockRoot = SszRoots.HashTreeRoot(block);
        ExtendPubkeys(postState);

        // Proposer boost for the first block of the slot arriving in the attesting interval.
        ulong timeIntoSlot = (Time - GenesisTime) % _spec.SecondsPerSlot;
        bool isBeforeAttestingInterval = timeIntoSlot < _spec.SecondsPerSlot / Presets.IntervalsPerSlot;
        if (block.Slot == _store.CurrentSlot && isBeforeAttestingInterval && _store.ProposerBoostRoot == Hash256.Zero)
            _store.ProposerBoostRoot = blockRoot;

        CheckpointRef stateJustified = CheckpointRef.From(postState.CurrentJustifiedCheckpoint!);
        CheckpointRef stateFinalized = CheckpointRef.From(postState.FinalizedCheckpoint!);
        _store.UpdateCheckpoints(stateJustified, stateFinalized);

        // The spec's compute_pulled_up_tip: eagerly run the justification weighing on the
        // post-state; for blocks from prior epochs the unrealized values are already realized.
        JustificationAndFinalizationState pulledUp = EpochProcessing.ComputeJustificationAndFinalization(postState, new EpochCache());
        CheckpointRef unrealizedJustified = CheckpointRef.From(pulledUp.CurrentJustifiedCheckpoint);
        CheckpointRef unrealizedFinalized = CheckpointRef.From(pulledUp.FinalizedCheckpoint);
        _store.UpdateUnrealizedCheckpoints(unrealizedJustified, unrealizedFinalized);
        ulong blockEpoch = BeaconStateAccessors.ComputeEpochAtSlot(block.Slot);
        if (blockEpoch < _store.CurrentEpoch)
            _store.UpdateCheckpoints(unrealizedJustified, unrealizedFinalized);

        ulong epochStartSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(blockEpoch);
        Hash256 targetRoot = block.Slot == epochStartSlot ? blockRoot : postState.GetBlockRootAtSlot(epochStartSlot);

        _protoArray.ProcessBlock(
            new ProtoBlock(
                Slot: block.Slot,
                Root: blockRoot,
                ParentRoot: parentRoot,
                StateRoot: block.StateRoot!,
                TargetRoot: targetRoot,
                JustifiedCheckpoint: stateJustified,
                FinalizedCheckpoint: stateFinalized,
                ExecutionStatus: executionStatus,
                ExecutionBlockHash: block.Body?.ExecutionPayload?.BlockHash,
                UnrealizedJustifiedCheckpoint: unrealizedJustified,
                UnrealizedFinalizedCheckpoint: unrealizedFinalized),
            _store.CurrentSlot,
            _store.JustifiedCheckpoint,
            _store.FinalizedCheckpoint);
    }

    /// <summary>
    /// The spec's <c>on_attestation</c>: validates the attestation against the store, indexes and
    /// verifies it against the target checkpoint state, and records the LMD votes — queueing
    /// current-slot attestations until the next tick.
    /// </summary>
    /// <param name="isFromBlock">Whether the attestation came in a block body, which skips the wall-clock recency checks.</param>
    /// <param name="verifySignature">Skippable for attestations whose aggregate signature was already verified by the state transition.</param>
    /// <exception cref="ForkChoiceException">The attestation violates a <c>validate_on_attestation</c> rule or its signature is invalid.</exception>
    /// <exception cref="BeaconStateException">The attestation's bitfields are inconsistent with the target state's committees.</exception>
    public void OnAttestation(Attestation attestation, bool isFromBlock = false, bool verifySignature = true)
    {
        AttestationData data = attestation.Data!;
        CheckpointRef target = CheckpointRef.From(data.Target!);
        Hash256 beaconBlockRoot = data.BeaconBlockRoot!;

        if (!isFromBlock)
        {
            // The spec's validate_target_epoch_against_current_time.
            ulong currentEpoch = _store.CurrentEpoch;
            ulong previousEpoch = currentEpoch > Presets.GenesisEpoch ? currentEpoch - 1 : Presets.GenesisEpoch;
            if (target.Epoch != currentEpoch && target.Epoch != previousEpoch)
                throw new ForkChoiceException($"Attestation target epoch {target.Epoch} is not the current or previous epoch ({currentEpoch})");
            if (data.Slot > _store.CurrentSlot)
                throw new ForkChoiceException($"Attestation for slot {data.Slot} is from the future (current slot {_store.CurrentSlot})");
        }

        if (target.Epoch != BeaconStateAccessors.ComputeEpochAtSlot(data.Slot))
            throw new ForkChoiceException($"Attestation target epoch {target.Epoch} does not match its slot {data.Slot}");
        if (!_protoArray.ContainsBlock(target.Root))
            throw new ForkChoiceException($"Attestation target {target.Root} is unknown to fork choice");
        if (_protoArray.GetBlockSlot(beaconBlockRoot) is not ulong blockSlot)
            throw new ForkChoiceException($"Attestation head block {beaconBlockRoot} is unknown to fork choice");
        if (blockSlot > data.Slot)
            throw new ForkChoiceException($"Attestation for slot {data.Slot} votes for the newer block at slot {blockSlot}");
        // The LMD vote must be consistent with the FFG vote target.
        if (GetCheckpointBlock(beaconBlockRoot, target.Epoch) != target.Root)
            throw new ForkChoiceException($"Attestation target {target.Root} is not the head block's ancestor at the target epoch start");

        BeaconStateFulu targetState = GetCheckpointState(target);
        CommitteeCache committees = _committees.GetCommitteeCache(targetState, target.Epoch);
        IndexedAttestation indexed = targetState.GetIndexedAttestation(attestation, committees);
        if (!BlockProcessing.IsValidIndexedAttestation(targetState, indexed, _pubkeys, verifySignature))
            throw new ForkChoiceException("Attestation indices or aggregate signature are invalid");

        // Attestations can only affect the fork choice of subsequent slots; current-slot
        // attestations wait in the queue until the next tick.
        if (!isFromBlock && data.Slot == _store.CurrentSlot)
        {
            _queuedAttestations.Add(new QueuedAttestation(data.Slot, indexed.AttestingIndices!, beaconBlockRoot, target.Epoch));
            return;
        }

        ApplyVotes(indexed.AttestingIndices!, beaconBlockRoot, target.Epoch);
    }

    /// <summary>
    /// The spec's <c>on_attester_slashing</c>: verifies both indexed attestations against the
    /// justified state and their slashability, then discounts the equivocating validators from all
    /// future <see cref="GetHead"/> computations.
    /// </summary>
    /// <param name="verifySignatures">Skippable for slashings whose signatures were already verified by the state transition.</param>
    /// <exception cref="ForkChoiceException">The slashing violates an <c>on_attester_slashing</c> assertion.</exception>
    public void OnAttesterSlashing(AttesterSlashing slashing, bool verifySignatures = true)
    {
        IndexedAttestation attestation1 = slashing.Attestation1!;
        IndexedAttestation attestation2 = slashing.Attestation2!;
        if (!BeaconStateAccessors.IsSlashableAttestationData(attestation1.Data!, attestation2.Data!))
            throw new ForkChoiceException("Attester slashing votes are not slashable");

        BeaconStateFulu justifiedState = _stateProvider.GetBlockState(_store.JustifiedCheckpoint.Root)
            ?? throw new ForkChoiceException($"No state for the justified root {_store.JustifiedCheckpoint.Root}");
        if (!BlockProcessing.IsValidIndexedAttestation(justifiedState, attestation1, _pubkeys, verifySignatures))
            throw new ForkChoiceException("Attester slashing attestation 1 is invalid");
        if (!BlockProcessing.IsValidIndexedAttestation(justifiedState, attestation2, _pubkeys, verifySignatures))
            throw new ForkChoiceException("Attester slashing attestation 2 is invalid");

        HashSet<ulong> indices2 = [.. attestation2.AttestingIndices!];
        foreach (ulong index in attestation1.AttestingIndices!)
        {
            if (indices2.Contains(index))
                _equivocatingIndices.Add(index);
        }
    }

    /// <summary>The spec's <c>get_head</c>: LMD-GHOST from the justified checkpoint, weighted by the justified state's balances and the proposer boost.</summary>
    public Hash256 GetHead()
    {
        JustifiedBalances balances = GetJustifiedBalances(_store.JustifiedCheckpoint);
        _protoArray.SetProposerBoostRoot(_store.ProposerBoostRoot);
        return _protoArray.GetHead(_store.JustifiedCheckpoint, _store.FinalizedCheckpoint, balances, _equivocatingIndices, _store.CurrentSlot);
    }

    /// <summary>Marks the payload of <paramref name="blockRoot"/> (and hence of all its ancestors) execution-valid.</summary>
    public void OnValidExecutionPayload(Hash256 blockRoot) => _protoArray.ProcessExecutionPayloadValidation(blockRoot);

    /// <summary>Invalidates the payload of <paramref name="blockRoot"/> and, when <paramref name="latestValidHash"/> identifies a known ancestor, everything between them, plus all descendants.</summary>
    public void OnInvalidExecutionPayload(Hash256 blockRoot, Hash256? latestValidHash = null) =>
        _protoArray.ProcessExecutionPayloadInvalidation(
            latestValidHash is null
                ? InvalidationOperation.InvalidateOne(blockRoot)
                : InvalidationOperation.InvalidateMany(blockRoot, alwaysInvalidateHead: true, latestValidHash),
            _store.FinalizedCheckpoint);

    /// <summary>The spec's <c>get_checkpoint_block</c>: the ancestor of <paramref name="root"/> at the start of <paramref name="epoch"/>.</summary>
    private Hash256 GetCheckpointBlock(Hash256 root, ulong epoch) =>
        _protoArray.GetAncestor(root, BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch))
            ?? throw new ForkChoiceException($"Block {root} is unknown to fork choice");

    /// <summary>The spec's <c>store_target_checkpoint_state</c>: the checkpoint's block state advanced to the checkpoint epoch start, cached.</summary>
    private BeaconStateFulu GetCheckpointState(CheckpointRef checkpoint)
    {
        if (_checkpointStates.TryGetValue(checkpoint, out BeaconStateFulu? cached))
            return cached;

        BeaconStateFulu state = _stateProvider.GetBlockState(checkpoint.Root)
            ?? throw new ForkChoiceException($"No state for the checkpoint {checkpoint}");
        ulong startSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(checkpoint.Epoch);
        if (state.Slot < startSlot)
        {
            state = _stateProvider.CopyBlockState(checkpoint.Root)!;
            SlotProcessing.ProcessSlots(state, startSlot, new EpochCache());
            // The epoch transitions above can apply pending deposits and grow the registry.
            ExtendPubkeys(state);
        }

        _checkpointStates[checkpoint] = state;
        return state;
    }

    /// <summary>The spec's <c>get_weight</c> balance source: effective balances of the justified state's active, unslashed validators.</summary>
    private JustifiedBalances GetJustifiedBalances(CheckpointRef justified)
    {
        if (_justifiedBalances.TryGetValue(justified, out JustifiedBalances? cached))
            return cached;

        BeaconStateFulu state = GetCheckpointState(justified);
        ulong epoch = state.GetCurrentEpoch();
        Validator[] validators = state.Validators!;
        ulong[] effectiveBalances = new ulong[validators.Length];
        for (int i = 0; i < validators.Length; i++)
        {
            Validator validator = validators[i];
            if (!validator.Slashed && validator.IsActiveValidator(epoch))
                effectiveBalances[i] = validator.EffectiveBalance;
        }

        JustifiedBalances balances = JustifiedBalances.FromEffectiveBalances(effectiveBalances);
        _justifiedBalances[justified] = balances;
        return balances;
    }

    private void DequeueAttestations()
    {
        for (int i = _queuedAttestations.Count - 1; i >= 0; i--)
        {
            QueuedAttestation queued = _queuedAttestations[i];
            if (queued.Slot < _store.CurrentSlot)
            {
                ApplyVotes(queued.AttestingIndices, queued.BlockRoot, queued.TargetEpoch);
                _queuedAttestations.RemoveAt(i);
            }
        }
    }

    /// <summary>The spec's <c>update_latest_messages</c>: equivocating validators never vote again.</summary>
    private void ApplyVotes(ulong[] attestingIndices, Hash256 blockRoot, ulong targetEpoch)
    {
        foreach (ulong index in attestingIndices)
        {
            if (!_equivocatingIndices.Contains(index))
                _protoArray.ProcessAttestation(index, blockRoot, targetEpoch);
        }
    }

    private void ExtendPubkeys(BeaconStateFulu state)
    {
        if (state.Validators!.Length > _pubkeys.Count)
            _pubkeys.Extend(state.Validators, _pubkeys.Count);
    }
}
