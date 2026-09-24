// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>
/// The spec-level fork-choice handlers (<c>on_tick</c>, <c>on_block</c>, <c>on_attestation</c>,
/// <c>on_attester_slashing</c>, <c>get_head</c>) over the proto-array implementation, following
/// the consensus-specs fork-choice document (Electra/Fulu rules), with Gloas blocks registered through
/// their own <see cref="OnBlock(SignedBeaconBlockGloas, BeaconStateGloas)"/> overload.
/// </summary>
/// <remarks>
/// Owns the spec <c>Store</c> state that is not in the proto-array: wall-clock time, the realized
/// and unrealized checkpoints (via <see cref="ForkChoiceStore"/>), the proposer boost root,
/// equivocating indices, queued current-slot attestations, and the derived checkpoint states and
/// justified balances (cached per checkpoint). Block post-states come from
/// <see cref="IForkChoiceStateProvider"/> for Fulu blocks and <see cref="IGloasBlockStateProvider"/> for
/// Gloas blocks, chosen by the fork of the block's own slot; the state transition itself stays outside - callers run
/// it and pass the post-state to <see cref="OnBlock"/>. Unrealized checkpoints are computed
/// clone-free via <see cref="EpochProcessing.ComputeJustificationAndFinalization"/> and
/// <see cref="GloasEpochProcessing.ComputeJustificationAndFinalization"/>.
/// Not thread-safe.
/// </remarks>
public sealed class ForkChoiceRunner
{
    /// <summary>Percent of the justified balance below which a head is "weak" enough to be overpowered by a proposer re-org; the spec's <c>REORG_HEAD_WEIGHT_THRESHOLD</c>.</summary>
    public const ulong ReorgHeadWeightThresholdPercent = 20;

    /// <summary>Percent of the justified balance above which a parent is "strong" enough to justify a proposer re-org; the spec's <c>REORG_PARENT_WEIGHT_THRESHOLD</c>.</summary>
    public const ulong ReorgParentWeightThresholdPercent = 160;

    /// <summary>Epochs since finality beyond which a proposer re-org is refused; the spec's <c>REORG_MAX_EPOCHS_SINCE_FINALIZATION</c>.</summary>
    public const ulong ReorgMaxEpochsSinceFinalization = 2;

    private readonly BeaconChainSpec _spec;
    private readonly IForkChoiceStateProvider _stateProvider;
    private readonly IGloasBlockStateProvider? _gloasStateProvider;
    private readonly PubkeyCache _pubkeys;
    private readonly ForkChoiceStore _store;
    private readonly ProtoArrayForkChoice _protoArray;
    private readonly HashSet<ulong> _equivocatingIndices = [];
    private readonly List<QueuedAttestation> _queuedAttestations = [];
    private readonly Dictionary<CheckpointRef, ForkedBeaconState> _checkpointStates = [];
    private readonly Dictionary<CheckpointRef, JustifiedBalances> _justifiedBalances = [];

    /// <summary>The spec's <c>store.block_timeliness</c>: whether each block arrived before its slot's attesting interval, keyed by block root.</summary>
    private readonly Dictionary<Hash256, bool> _blockTimeliness = [];

    /// <summary>The spec's <c>store.payloads</c>, as roots only: the Gloas blocks whose execution payload envelope was delivered and verified.</summary>
    private readonly HashSet<Hash256> _payloads = [];

    /// <summary>The committed bid's <c>parent_block_hash</c> of each Gloas block, keyed by block root.</summary>
    private readonly Dictionary<Hash256, Hash256> _parentBlockHashes = [];

    /// <summary>Committee shufflings only; safe to share across forks (keyed by decision root). The balance memo is never used through this instance.</summary>
    private readonly EpochCache _committees = new();

    /// <summary>An attestation for the current slot, validated and indexed, waiting for the next slot tick (the spec only counts attestations from past slots).</summary>
    private readonly record struct QueuedAttestation(ulong Slot, ulong[] AttestingIndices, Hash256 BlockRoot, ulong TargetEpoch);

    /// <summary>The fields an indexed attestation carries in both the Fulu and the Gloas container.</summary>
    private readonly record struct IndexedVote(ulong[] AttestingIndices, AttestationData Data, BlsSignature Signature);

    /// <summary>Creates the store from an anchor (the spec's <c>get_forkchoice_store</c>): the anchor becomes the justified and finalized checkpoint at its own epoch.</summary>
    /// <param name="anchorState">The post-state of <paramref name="anchorBlock"/>; also supplies the genesis time.</param>
    /// <param name="anchorBlock">The finalized block to root the block tree at.</param>
    /// <param name="gloasStateProvider">
    /// The post-states of Gloas blocks. Without it every Gloas block is refused, because none of its
    /// checkpoint states could ever be resolved.
    /// </param>
    /// <exception cref="ForkChoiceException">
    /// The anchor block's state root does not match the anchor state, or the anchor block is in a Gloas epoch.
    /// </exception>
    public ForkChoiceRunner(
        BeaconChainSpec spec,
        BeaconStateFulu anchorState,
        BeaconBlock anchorBlock,
        IForkChoiceStateProvider stateProvider,
        PubkeyCache pubkeys,
        IGloasBlockStateProvider? gloasStateProvider = null)
        : this(spec, stateProvider, pubkeys, gloasStateProvider, FuluAnchor(spec, anchorState, anchorBlock))
    {
    }

    /// <summary>
    /// Creates the store from a Gloas anchor (the Gloas <c>get_forkchoice_store</c>): the anchor becomes the
    /// justified and finalized checkpoint at its own epoch, and no payload is yet verified for it.
    /// </summary>
    /// <remarks>
    /// The spec's <c>payloads={}</c>: finality says nothing about whether the anchor's payload was revealed,
    /// so a child that builds on it waits for the anchor's envelope to be verified like any other. The anchor
    /// node is <see cref="ExecutionStatus.Valid"/> and carries the committed bid's <c>block_hash</c>, the hash
    /// every Gloas node carries; that hash need not exist on the execution layer when the payload was empty.
    /// </remarks>
    /// <param name="anchorState">The post-state of <paramref name="anchorBlock"/>; also supplies the genesis time.</param>
    /// <param name="anchorBlock">The finalized Gloas block to root the block tree at.</param>
    /// <param name="gloasStateProvider">The post-states of Gloas blocks, the anchor's included: its checkpoint state resolves through it.</param>
    /// <exception cref="ForkChoiceException">
    /// The anchor block's state root does not match the anchor state, or the anchor block is before the Gloas fork.
    /// </exception>
    public ForkChoiceRunner(
        BeaconChainSpec spec,
        BeaconStateGloas anchorState,
        BeaconBlockGloas anchorBlock,
        IForkChoiceStateProvider stateProvider,
        PubkeyCache pubkeys,
        IGloasBlockStateProvider gloasStateProvider)
        : this(spec, stateProvider, pubkeys, gloasStateProvider, GloasAnchor(spec, anchorState, anchorBlock))
    {
    }

    private ForkChoiceRunner(
        BeaconChainSpec spec,
        IForkChoiceStateProvider stateProvider,
        PubkeyCache pubkeys,
        IGloasBlockStateProvider? gloasStateProvider,
        AnchorNode anchor)
    {
        _spec = spec;
        _stateProvider = stateProvider;
        _gloasStateProvider = gloasStateProvider;
        _pubkeys = pubkeys;
        GenesisTime = anchor.GenesisTime;
        Time = GenesisTime + spec.SecondsPerSlot * anchor.StateSlot;

        CheckpointRef anchorCheckpoint = new(anchor.Epoch, anchor.Root);
        _store = new ForkChoiceStore(spec.SlotsPerEpoch, anchor.StateSlot, anchorCheckpoint, anchorCheckpoint);
        _protoArray = new ProtoArrayForkChoice(
            currentSlot: anchor.StateSlot,
            finalizedBlockSlot: anchor.BlockSlot,
            finalizedBlockStateRoot: anchor.StateRoot,
            justifiedCheckpoint: anchorCheckpoint,
            finalizedCheckpoint: anchorCheckpoint,
            executionStatus: anchor.ExecutionBlockHash is null ? ExecutionStatus.Irrelevant : ExecutionStatus.Valid,
            executionBlockHash: anchor.ExecutionBlockHash,
            slotsPerEpoch: spec.SlotsPerEpoch);
    }

    /// <summary>The fork-independent parts of an anchor that <c>get_forkchoice_store</c> reads.</summary>
    private readonly record struct AnchorNode(ulong GenesisTime, ulong StateSlot, ulong Epoch, ulong BlockSlot, Hash256 Root, Hash256 StateRoot, Hash256? ExecutionBlockHash);

    private static AnchorNode FuluAnchor(BeaconChainSpec spec, BeaconStateFulu anchorState, BeaconBlock anchorBlock)
    {
        if (anchorBlock.StateRoot != SszRoots.HashTreeRoot(anchorState))
            throw new ForkChoiceException("Anchor block state root does not match the anchor state");
        // Every block state lookup picks the provider by slot, so a Gloas-epoch root must never be a Fulu node.
        if (SignedBeaconBlockCodec.IsGloasSlot(anchorBlock.Slot, spec))
            throw new ForkChoiceException($"Anchor block at slot {anchorBlock.Slot} is in a Gloas epoch; it must be a {nameof(BeaconBlockGloas)}");

        return new AnchorNode(
            anchorState.GenesisTime,
            anchorState.Slot,
            anchorState.GetCurrentEpoch(),
            anchorBlock.Slot,
            SszRoots.HashTreeRoot(anchorBlock),
            anchorBlock.StateRoot!,
            anchorBlock.Body?.ExecutionPayload?.BlockHash);
    }

    private static AnchorNode GloasAnchor(BeaconChainSpec spec, BeaconStateGloas anchorState, BeaconBlockGloas anchorBlock)
    {
        // specs/gloas/fork-choice.md get_forkchoice_store: assert anchor_block.state_root == hash_tree_root(anchor_state).
        if (anchorBlock.StateRoot != SszRoots.HashTreeRoot(anchorState))
            throw new ForkChoiceException("Anchor block state root does not match the anchor state");
        if (!SignedBeaconBlockCodec.IsGloasSlot(anchorBlock.Slot, spec))
            throw new ForkChoiceException($"Anchor block at slot {anchorBlock.Slot} is before the Gloas fork; it must be a {nameof(BeaconBlock)}");

        return new AnchorNode(
            anchorState.GenesisTime,
            anchorState.Slot,
            anchorState.GetCurrentEpoch(),
            anchorBlock.Slot,
            SszRoots.HashTreeRoot(anchorBlock),
            anchorBlock.StateRoot!,
            anchorBlock.Body!.SignedExecutionPayloadBid!.Message!.BlockHash!);
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

    /// <summary>An immutable copy of the store for readers off the import thread; see <see cref="ForkChoiceSnapshot"/>.</summary>
    /// <remarks>O(nodes): the tree is pruned at finalization, so this stays a few hundred entries and is taken on every head computation.</remarks>
    public ForkChoiceSnapshot Snapshot()
    {
        IReadOnlyList<ProtoNode> nodes = _protoArray.Nodes;
        ForkChoiceSnapshotNode[] copy = new ForkChoiceSnapshotNode[nodes.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            ProtoNode node = nodes[i];
            copy[i] = new ForkChoiceSnapshotNode(
                node.Slot,
                node.Root,
                node.Parent is int parent ? nodes[parent].Root : null,
                node.JustifiedCheckpoint.Epoch,
                node.FinalizedCheckpoint.Epoch,
                node.Weight,
                node.ExecutionStatus,
                node.ExecutionBlockHash);
        }

        return new ForkChoiceSnapshot(_store.JustifiedCheckpoint, _store.FinalizedCheckpoint, _store.ProposerBoostRoot, copy);
    }

    /// <summary>
    /// Prunes fork-choice state below the finalized checkpoint: the proto-array block tree (subject
    /// to its prune threshold), the cached checkpoint states and justified balances of epochs
    /// before the finalized one, and the per-block records of every block the tree no longer holds.
    /// </summary>
    public void Prune()
    {
        CheckpointRef finalized = _store.FinalizedCheckpoint;
        _protoArray.MaybePrune(finalized.Root);
        PruneCheckpointCache(_checkpointStates, finalized.Epoch);
        PruneCheckpointCache(_justifiedBalances, finalized.Epoch);
        PruneUnknownRoots(_blockTimeliness);
        PruneUnknownRoots(_parentBlockHashes);
        _payloads.RemoveWhere(root => !_protoArray.ContainsBlock(root));
    }

    private void PruneUnknownRoots<TValue>(Dictionary<Hash256, TValue> byRoot)
    {
        List<Hash256>? stale = null;
        foreach (Hash256 root in byRoot.Keys)
        {
            if (!_protoArray.ContainsBlock(root)) (stale ??= []).Add(root);
        }

        if (stale is not null)
        {
            foreach (Hash256 root in stale) byRoot.Remove(root);
        }
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
    /// <remarks>
    /// This does not replay the block's body operations. The spec's <c>on_block</c> does not either:
    /// the fork_choice test format treats an <c>on_block</c> step as implying <c>on_attestation</c>
    /// for every body attestation and <c>on_attester_slashing</c> for every body attester slashing,
    /// and that convention is the caller's to honour.
    /// After this returns, the caller must feed <c>Body.Attestations</c> to
    /// <see cref="OnAttestation"/> with <c>isFromBlock: true</c> and <c>Body.AttesterSlashings</c>
    /// to <see cref="OnAttesterSlashing"/>, both with signature verification off (the transition
    /// already verified them). Whether a body operation the store refuses (typically an attestation
    /// for a head or target block this node never saw) is tolerated or fatal is the caller's policy;
    /// the block itself is in the tree either way. A caller that skips the replay loses LMD votes
    /// and equivocation discounts silently: <see cref="GetHead"/> keeps answering, from fewer votes.
    /// </remarks>
    /// <param name="executionStatus">The execution layer's verdict on the block's payload, usually <see cref="ExecutionStatus.Optimistic"/> until <c>newPayload</c> completes.</param>
    /// <param name="dataColumns">
    /// The full column set retrieved for this block's blob commitments (the spec's
    /// <c>retrieve_column_sidecars</c> as a supernode sees it). Calling this overload selects
    /// <see cref="FullColumnSetAvailability"/>: every one of the 128 columns must be present and verify.
    /// That is the consensus-spec vectors' rule and impossible for a partial-custody node; production
    /// callers must use the <see cref="IDataAvailabilityRule"/> overload instead.
    /// </param>
    /// <exception cref="ForkChoiceException">The block violates an <c>on_block</c> assertion, or its data is not available.</exception>
    public void OnBlock(SignedBeaconBlock signedBlock, BeaconStateFulu postState, ExecutionStatus executionStatus, IReadOnlyList<DataColumnSidecar>? dataColumns) =>
        OnBlock(signedBlock, postState, executionStatus, new FullColumnSetAvailability(dataColumns));

    /// <inheritdoc cref="OnBlock(SignedBeaconBlock, BeaconStateFulu, ExecutionStatus, IReadOnlyList{DataColumnSidecar})"/>
    /// <param name="availability">
    /// The caller's explicit reading of <c>is_data_available</c>; see <see cref="IDataAvailabilityRule"/> for
    /// why the two rules cannot be inferred from each other.
    /// </param>
    public void OnBlock(SignedBeaconBlock signedBlock, BeaconStateFulu postState, ExecutionStatus executionStatus, IDataAvailabilityRule availability)
    {
        BeaconBlock block = signedBlock.Message!;
        if (IsGloasSlot(block.Slot))
            throw new ForkChoiceException($"Block at slot {block.Slot} is in a Gloas epoch; it must be a {nameof(SignedBeaconBlockGloas)}");
        Hash256 parentRoot = block.ParentRoot!;
        ValidateOnBlock(block.Slot, parentRoot);

        Hash256 blockRoot = SszRoots.HashTreeRoot(block);
        if (!availability.IsDataAvailable(block, blockRoot, _spec))
            throw new ForkChoiceException($"Block {blockRoot} at slot {block.Slot} does not have all its blob data available");
        ExtendPubkeys(postState.Validators!);

        RegisterBlock(
            block.Slot,
            blockRoot,
            parentRoot,
            block.StateRoot!,
            CheckpointRef.From(postState.CurrentJustifiedCheckpoint!),
            CheckpointRef.From(postState.FinalizedCheckpoint!),
            EpochProcessing.ComputeJustificationAndFinalization(postState, new EpochCache()),
            executionStatus,
            block.Body?.ExecutionPayload?.BlockHash);
    }

    /// <summary>
    /// The Gloas <c>on_block</c>, minus the state transition: the caller has already computed
    /// <paramref name="postState"/> by applying <paramref name="signedBlock"/> to its parent state.
    /// Validates the block against the store, applies the proposer boost when timely, updates the
    /// realized and unrealized checkpoints, and registers the block with the proto-array.
    /// </summary>
    /// <remarks>
    /// The block is registered <see cref="ExecutionStatus.Optimistic"/> with the committed bid's
    /// <c>block_hash</c> as its execution block hash: under EIP-7732 the block carries only the bid and
    /// its payload arrives later in an envelope, and the invalidation walk maps a <c>latestValidHash</c>
    /// to roots through that hash, so the parent's applied hash would be wrong there.
    /// There is no availability argument because the Gloas <c>on_block</c> no longer calls
    /// <c>is_data_available</c>. The body replay contract is the one the Fulu overload documents, through
    /// the <see cref="AttestationGloas"/> and <see cref="AttesterSlashingGloas"/> overloads.
    /// A block that builds on its parent's full payload (<see cref="IsParentNodeFull"/>) is refused until
    /// that payload is recorded through <see cref="OnExecutionPayloadVerified"/>; the refusal leaves the store untouched.
    /// </remarks>
    /// <exception cref="ForkChoiceException">
    /// The block's slot is before the Gloas fork, this runner has no <see cref="IGloasBlockStateProvider"/>,
    /// or the block violates an <c>on_block</c> assertion, including a full parent whose payload is not verified.
    /// </exception>
    public void OnBlock(SignedBeaconBlockGloas signedBlock, BeaconStateGloas postState)
    {
        BeaconBlockGloas block = signedBlock.Message!;
        if (!IsGloasSlot(block.Slot))
            throw new ForkChoiceException($"Block at slot {block.Slot} is before the Gloas fork; it must be a {nameof(SignedBeaconBlock)}");
        if (_gloasStateProvider is null)
            throw new ForkChoiceException($"Block at slot {block.Slot} is a Gloas block, but no {nameof(IGloasBlockStateProvider)} was supplied to resolve its checkpoint states");
        Hash256 parentRoot = block.ParentRoot!;
        ValidateOnBlock(block.Slot, parentRoot);
        // specs/gloas/fork-choice.md on_block: if is_parent_node_full, assert is_payload_verified(parent_root).
        if (IsParentNodeFull(block) && !IsPayloadVerified(parentRoot))
            throw new ForkChoiceException($"Block at slot {block.Slot} builds on the full payload of {parentRoot}, which is not verified");

        Hash256 blockRoot = SszRoots.HashTreeRoot(block);
        ExtendPubkeys(postState.Validators!);

        ExecutionPayloadBid bid = block.Body!.SignedExecutionPayloadBid!.Message!;
        RegisterBlock(
            block.Slot,
            blockRoot,
            parentRoot,
            block.StateRoot!,
            CheckpointRef.From(postState.CurrentJustifiedCheckpoint!),
            CheckpointRef.From(postState.FinalizedCheckpoint!),
            GloasEpochProcessing.ComputeJustificationAndFinalization(postState, new EpochCache()),
            ExecutionStatus.Optimistic,
            bid.BlockHash!);
        _parentBlockHashes[blockRoot] = bid.ParentBlockHash!;
    }

    /// <summary>
    /// The spec's <c>is_payload_verified</c>: whether the execution payload of <paramref name="blockRoot"/>
    /// has been delivered and verified.
    /// </summary>
    /// <remarks>
    /// A known pre-Gloas block counts as verified: its payload came inside the block and went through
    /// <c>newPayload</c> at import, and <c>upgrade_to_gloas</c> seeds <c>latest_block_hash</c> from it, so the
    /// first Gloas block always builds on it full. The spec does not define this boundary case; a literal
    /// <c>root in store.payloads</c> would refuse every first Gloas block. A Gloas block counts once
    /// <see cref="OnExecutionPayloadVerified"/> recorded it, and an unknown block never does.
    /// </remarks>
    public bool IsPayloadVerified(Hash256 blockRoot) =>
        _protoArray.GetBlockSlot(blockRoot) is ulong slot && (!IsGloasSlot(slot) || _payloads.Contains(blockRoot));

    /// <summary>
    /// The spec's <c>is_parent_node_full</c>: whether <paramref name="block"/>'s bid builds on its parent's
    /// execution payload rather than on the payload before it.
    /// </summary>
    /// <remarks>
    /// The spec's <c>get_parent_payload_status</c> compares the bid's <c>parent_block_hash</c> with the parent's
    /// bid <c>block_hash</c>, which is the execution block hash a Gloas node is registered with; a Fulu parent's is
    /// its own payload's hash. An unknown parent is not full.
    /// </remarks>
    public bool IsParentNodeFull(BeaconBlockGloas block) =>
        block.Body!.SignedExecutionPayloadBid!.Message!.ParentBlockHash == _protoArray.GetExecutionBlockHash(block.ParentRoot!);

    /// <summary>
    /// Records that the execution payload envelope of <paramref name="blockRoot"/> was delivered and verified:
    /// the spec's <c>store.payloads</c> write in <c>on_execution_payload_envelope</c>. Idempotent.
    /// </summary>
    /// <exception cref="ForkChoiceException">The block is unknown to fork choice (the spec asserts it is in <c>store.block_states</c>), or is a pre-Gloas block, which has no envelope.</exception>
    public void OnExecutionPayloadVerified(Hash256 blockRoot)
    {
        ulong slot = _protoArray.GetBlockSlot(blockRoot) ?? throw new ForkChoiceException($"Block {blockRoot} is unknown to fork choice");
        if (!IsGloasSlot(slot))
            throw new ForkChoiceException($"Block {blockRoot} at slot {slot} is before the Gloas fork; its payload has no envelope");
        _payloads.Add(blockRoot);
    }

    /// <summary>The committed bid's <c>parent_block_hash</c> of the Gloas block <paramref name="blockRoot"/>; <see langword="null"/> for a pre-Gloas or unknown block.</summary>
    public Hash256? GetParentBlockHash(Hash256 blockRoot) => _parentBlockHashes.GetValueOrDefault(blockRoot);

    /// <summary>The fork-independent <c>on_block</c> assertions: a known parent whose payload is not invalid, not from the future, after the finalized slot and descending from the finalized checkpoint.</summary>
    private void ValidateOnBlock(ulong slot, Hash256 parentRoot)
    {
        CheckpointRef finalized = _store.FinalizedCheckpoint;
        if (!_protoArray.ContainsBlock(parentRoot))
            throw new ForkChoiceException($"Parent {parentRoot} of the block at slot {slot} is unknown to fork choice");
        // specs/bellatrix/optimistic-sync.md: the parent of the block MUST NOT have an INVALIDATED execution payload.
        if (_protoArray.GetBlockExecutionStatus(parentRoot) == ExecutionStatus.Invalid)
            throw new ForkChoiceException($"Parent {parentRoot} of the block at slot {slot} has an invalid execution payload");
        if (slot > _store.CurrentSlot)
            throw new ForkChoiceException($"Block at slot {slot} is from the future (current slot {_store.CurrentSlot})");
        ulong finalizedSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(finalized.Epoch);
        if (slot <= finalizedSlot)
            throw new ForkChoiceException($"Block at slot {slot} is not after the finalized slot {finalizedSlot}");
        if (GetCheckpointBlock(parentRoot, finalized.Epoch) != finalized.Root)
            throw new ForkChoiceException($"Block at slot {slot} does not descend from the finalized checkpoint {finalized}");
    }

    /// <summary>The store updates of an accepted <c>on_block</c>: proposer boost, realized and unrealized checkpoints, and the proto-array node.</summary>
    /// <param name="pulledUp">The justification weighing run on the block's post-state; the spec's <c>compute_pulled_up_tip</c>.</param>
    private void RegisterBlock(
        ulong slot,
        Hash256 blockRoot,
        Hash256 parentRoot,
        Hash256 stateRoot,
        CheckpointRef stateJustified,
        CheckpointRef stateFinalized,
        JustificationAndFinalizationState pulledUp,
        ExecutionStatus executionStatus,
        Hash256? executionBlockHash)
    {
        // Checked before the first store update: the proto-array would refuse it only after the boost and checkpoints moved.
        if ((executionStatus == ExecutionStatus.Irrelevant) != (executionBlockHash is null))
            throw new ForkChoiceException($"Block {blockRoot} must carry an execution block hash if and only if execution is enabled");

        // Proposer boost for the first block of the slot arriving in the attesting interval.
        ulong timeIntoSlot = (Time - GenesisTime) % _spec.SecondsPerSlot;
        bool isBeforeAttestingInterval = timeIntoSlot < _spec.SecondsPerSlot / Presets.IntervalsPerSlot;
        bool isTimely = slot == _store.CurrentSlot && isBeforeAttestingInterval;
        _blockTimeliness[blockRoot] = isTimely;
        if (isTimely && _store.ProposerBoostRoot == Hash256.Zero)
            _store.ProposerBoostRoot = blockRoot;

        _store.UpdateCheckpoints(stateJustified, stateFinalized);

        // For blocks from prior epochs the unrealized values are already realized.
        CheckpointRef unrealizedJustified = CheckpointRef.From(pulledUp.CurrentJustifiedCheckpoint);
        CheckpointRef unrealizedFinalized = CheckpointRef.From(pulledUp.FinalizedCheckpoint);
        _store.UpdateUnrealizedCheckpoints(unrealizedJustified, unrealizedFinalized);
        if (BeaconStateAccessors.ComputeEpochAtSlot(slot) < _store.CurrentEpoch)
            _store.UpdateCheckpoints(unrealizedJustified, unrealizedFinalized);

        _protoArray.ProcessBlock(
            new ProtoBlock(
                Slot: slot,
                Root: blockRoot,
                ParentRoot: parentRoot,
                StateRoot: stateRoot,
                JustifiedCheckpoint: stateJustified,
                FinalizedCheckpoint: stateFinalized,
                ExecutionStatus: executionStatus,
                ExecutionBlockHash: executionBlockHash,
                UnrealizedJustifiedCheckpoint: unrealizedJustified,
                UnrealizedFinalizedCheckpoint: unrealizedFinalized),
            _store.CurrentSlot,
            _store.JustifiedCheckpoint,
            _store.FinalizedCheckpoint);
    }

    /// <summary>Whether <paramref name="slot"/> is in an epoch at or after <see cref="BeaconChainSpec.GloasForkEpoch"/>.</summary>
    /// <remarks>Compares the epoch directly: <see cref="BeaconChainSpec.ForkAtEpoch"/> throws for pre-Electra epochs, which the mainnet fork-choice vectors use.</remarks>
    private bool IsGloasSlot(ulong slot) => _spec.GetEpoch(slot) >= _spec.GloasForkEpoch;

    /// <summary>
    /// The spec's <c>on_attestation</c>: validates the attestation against the store, indexes and
    /// verifies it against the target checkpoint state, and records the LMD votes — queueing
    /// current-slot attestations until the next tick.
    /// </summary>
    /// <remarks>
    /// The committees and the signature domain come from the target checkpoint state, whose fork is
    /// that of the target epoch rather than of the container: a Gloas block's body can carry a vote
    /// targeting a Fulu epoch, and a checkpoint rooted in a Fulu block can be a Gloas state.
    /// </remarks>
    /// <param name="isFromBlock">Whether the attestation came in a block body, which skips the wall-clock recency checks.</param>
    /// <param name="verifySignature">Skippable for attestations whose aggregate signature was already verified by the state transition.</param>
    /// <exception cref="ForkChoiceException">The attestation violates a <c>validate_on_attestation</c> rule or its signature is invalid.</exception>
    /// <exception cref="BeaconStateException">The attestation's bitfields are inconsistent with the target state's committees.</exception>
    public void OnAttestation(Attestation attestation, bool isFromBlock = false, bool verifySignature = true) =>
        OnAttestation(attestation.Data!, attestation.AggregationBits!, attestation.CommitteeBits!, attestation.Signature, isFromBlock, verifySignature, gloasContainer: false);

    /// <inheritdoc cref="OnAttestation(Attestation, bool, bool)"/>
    /// <remarks>The Gloas <c>is_valid_indexed_attestation</c> bound on the attesting indices applies before any signature is aggregated, even against a Fulu target state.</remarks>
    public void OnAttestation(AttestationGloas attestation, bool isFromBlock = false, bool verifySignature = true) =>
        OnAttestation(attestation.Data!, attestation.AggregationBits!, attestation.CommitteeBits!, attestation.Signature, isFromBlock, verifySignature, gloasContainer: true);

    private void OnAttestation(AttestationData data, BitArray aggregationBits, BitArray committeeBits, BlsSignature signature, bool isFromBlock, bool verifySignature, bool gloasContainer)
    {
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
        if (gloasContainer || IsGloasSlot(data.Slot))
            ValidatePayloadStatusVote(data, blockSlot);
        // The LMD vote must be consistent with the FFG vote target.
        if (GetCheckpointBlock(beaconBlockRoot, target.Epoch) != target.Root)
            throw new ForkChoiceException($"Attestation target {target.Root} is not the head block's ancestor at the target epoch start");

        ForkedBeaconState targetState = GetCheckpointState(target);
        ulong[] attestingIndices = targetState switch
        {
            ForkedBeaconState.OfFulu fulu => fulu.State.GetAttestingIndices(
                new Attestation { AggregationBits = aggregationBits, Data = data, Signature = signature, CommitteeBits = committeeBits },
                _committees.GetCommitteeCache(fulu.State, target.Epoch)),
            ForkedBeaconState.OfGloas gloas => gloas.State.GetAttestingIndices(
                new AttestationGloas { AggregationBits = aggregationBits, Data = data, Signature = signature, CommitteeBits = committeeBits },
                _committees.GetCommitteeCache(gloas.State, target.Epoch)),
            _ => throw new NotSupportedException($"Unhandled checkpoint state {targetState.GetType().Name}"),
        };
        if (gloasContainer)
            ThrowIfOverGloasIndexedAttestationBound(attestingIndices, "Attestation");
        if (!IsValidIndexedAttestation(targetState, new IndexedVote(attestingIndices, data, signature), verifySignature))
            throw new ForkChoiceException("Attestation indices or aggregate signature are invalid");

        // Attestations can only affect the fork choice of subsequent slots; current-slot
        // attestations wait in the queue until the next tick.
        if (!isFromBlock && data.Slot == _store.CurrentSlot)
        {
            _queuedAttestations.Add(new QueuedAttestation(data.Slot, attestingIndices, beaconBlockRoot, target.Epoch));
            return;
        }

        ApplyVotes(attestingIndices, beaconBlockRoot, target.Epoch);
    }

    /// <summary>
    /// The Gloas <c>validate_on_attestation</c> rules on <c>data.index</c>, which votes for the head block's
    /// payload status (specs/gloas/fork-choice.md, EIP-7732): 0 or 1, 0 for a vote in the block's own slot,
    /// and 1 only for a block whose payload is verified.
    /// </summary>
    private void ValidatePayloadStatusVote(AttestationData data, ulong blockSlot)
    {
        if (data.Index > 1)
            throw new ForkChoiceException($"Attestation index {data.Index} is not a payload status (0 or 1)");
        if (blockSlot == data.Slot && data.Index != 0)
            throw new ForkChoiceException($"Attestation for slot {data.Slot} votes for the payload of a block from its own slot");
        if (data.Index == 1 && !IsPayloadVerified(data.BeaconBlockRoot!))
            throw new ForkChoiceException($"Attestation votes for the payload of {data.BeaconBlockRoot}, which is not verified");
    }

    /// <summary>
    /// The spec's <c>on_attester_slashing</c>: verifies both indexed attestations against the
    /// justified state and their slashability, then discounts the equivocating validators from all
    /// future <see cref="GetHead"/> computations.
    /// </summary>
    /// <remarks>The justified state is the justified block's own post-state, of that block's fork whichever container carried the slashing.</remarks>
    /// <param name="verifySignatures">Skippable for slashings whose signatures were already verified by the state transition.</param>
    /// <exception cref="ForkChoiceException">The slashing violates an <c>on_attester_slashing</c> assertion.</exception>
    public void OnAttesterSlashing(AttesterSlashing slashing, bool verifySignatures = true)
    {
        IndexedAttestation attestation1 = slashing.Attestation1!;
        IndexedAttestation attestation2 = slashing.Attestation2!;
        OnAttesterSlashing(
            new IndexedVote(attestation1.AttestingIndices!, attestation1.Data!, attestation1.Signature),
            new IndexedVote(attestation2.AttestingIndices!, attestation2.Data!, attestation2.Signature),
            verifySignatures);
    }

    /// <inheritdoc cref="OnAttesterSlashing(AttesterSlashing, bool)"/>
    /// <remarks>
    /// The Gloas <c>is_valid_indexed_attestation</c> bound on the attesting indices, which the container's
    /// progressive list no longer carries, is enforced for both attestations before any signature is
    /// aggregated, whichever fork the justified state is; the signature domain stays that state's own.
    /// </remarks>
    public void OnAttesterSlashing(AttesterSlashingGloas slashing, bool verifySignatures = true)
    {
        IndexedAttestationGloas attestation1 = slashing.Attestation1!;
        IndexedAttestationGloas attestation2 = slashing.Attestation2!;
        ThrowIfOverGloasIndexedAttestationBound(attestation1.AttestingIndices, "Attester slashing attestation 1");
        ThrowIfOverGloasIndexedAttestationBound(attestation2.AttestingIndices, "Attester slashing attestation 2");
        OnAttesterSlashing(
            new IndexedVote(attestation1.AttestingIndices!, attestation1.Data!, attestation1.Signature),
            new IndexedVote(attestation2.AttestingIndices!, attestation2.Data!, attestation2.Signature),
            verifySignatures);
    }

    private void OnAttesterSlashing(IndexedVote attestation1, IndexedVote attestation2, bool verifySignatures)
    {
        if (!BeaconStateAccessors.IsSlashableAttestationData(attestation1.Data, attestation2.Data))
            throw new ForkChoiceException("Attester slashing votes are not slashable");

        ForkedBeaconState justifiedState = GetBlockState(_store.JustifiedCheckpoint.Root);
        if (!IsValidIndexedAttestation(justifiedState, attestation1, verifySignatures))
            throw new ForkChoiceException("Attester slashing attestation 1 is invalid");
        if (!IsValidIndexedAttestation(justifiedState, attestation2, verifySignatures))
            throw new ForkChoiceException("Attester slashing attestation 2 is invalid");

        HashSet<ulong> indices2 = [.. attestation2.AttestingIndices];
        foreach (ulong index in attestation1.AttestingIndices)
        {
            if (indices2.Contains(index))
                _equivocatingIndices.Add(index);
        }
    }

    /// <summary>The Gloas <c>is_valid_indexed_attestation</c> length bound (specs/gloas/beacon-chain.md, EIP-7688): at most <c>MAX_VALIDATORS_PER_COMMITTEE * MAX_COMMITTEES_PER_SLOT</c> attesting indices.</summary>
    private static void ThrowIfOverGloasIndexedAttestationBound(ulong[]? attestingIndices, string what)
    {
        const int MaxAttestingIndices = Presets.MaxValidatorsPerCommittee * Presets.MaxCommitteesPerSlot;
        int count = attestingIndices?.Length ?? 0;
        if (count > MaxAttestingIndices)
            throw new ForkChoiceException($"{what} has {count} attesting indices, over the bound of {MaxAttestingIndices}");
    }

    /// <summary>The spec's <c>is_valid_indexed_attestation</c> of <paramref name="state"/>'s fork, over the vote in that fork's container.</summary>
    private bool IsValidIndexedAttestation(ForkedBeaconState state, IndexedVote vote, bool verifySignature) => state switch
    {
        ForkedBeaconState.OfFulu fulu => BlockProcessing.IsValidIndexedAttestation(
            fulu.State,
            new IndexedAttestation { AttestingIndices = vote.AttestingIndices, Data = vote.Data, Signature = vote.Signature },
            _pubkeys,
            verifySignature),
        ForkedBeaconState.OfGloas gloas => GloasBlockProcessing.IsValidIndexedAttestation(
            gloas.State,
            new IndexedAttestationGloas { AttestingIndices = vote.AttestingIndices, Data = vote.Data, Signature = vote.Signature },
            _pubkeys,
            verifySignature),
        _ => throw new NotSupportedException($"Unhandled state {state.GetType().Name}"),
    };

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

    /// <summary>
    /// The spec's <c>get_proposer_head</c>: the block the proposer of <paramref name="proposalSlot"/> should
    /// build on - <paramref name="headRoot"/>'s parent when <see cref="ShouldOverrideForkchoiceUpdate"/> says
    /// to re-org the late, weakly-attested head out; <paramref name="headRoot"/> itself otherwise.
    /// </summary>
    public Hash256 GetProposerHead(Hash256 headRoot, ulong proposalSlot) =>
        ShouldOverrideForkchoiceUpdate(headRoot, proposalSlot) ? _protoArray.GetParentRoot(headRoot)! : headRoot;

    /// <summary>
    /// The spec's <c>should_override_forkchoice_update</c>: whether the proposer of <paramref name="proposalSlot"/>
    /// should re-org out <paramref name="headRoot"/> and build on its parent instead, because the head arrived
    /// late, is weakly attested, and the parent is strong enough to safely take its place.
    /// </summary>
    /// <remarks>
    /// Every condition below must hold for the re-org to happen; consensus-specs' <c>get_proposer_head</c>
    /// fork-choice.md documents each one. Weights are refreshed via <see cref="GetHead"/> first: the spec's
    /// own <c>get_weight</c> is a live computation over current votes and the (already-settled) proposer
    /// boost, not a value cached from a stale <see cref="GetHead"/> call the caller happened to make earlier.
    /// </remarks>
    /// <exception cref="ForkChoiceException"><paramref name="headRoot"/> or its parent is unknown to fork choice, or the head still holds the proposer boost (its score has not settled).</exception>
    public bool ShouldOverrideForkchoiceUpdate(Hash256 headRoot, ulong proposalSlot)
    {
        Hash256 parentRoot = _protoArray.GetParentRoot(headRoot)
            ?? throw new ForkChoiceException($"Block {headRoot} is unknown to fork choice, or has no parent to reorg onto");
        ulong headSlot = _protoArray.GetBlockSlot(headRoot) ?? throw new ForkChoiceException($"Block {headRoot} is unknown to fork choice");
        ulong parentSlot = _protoArray.GetBlockSlot(parentRoot) ?? throw new ForkChoiceException($"Block {parentRoot} is unknown to fork choice");

        if (_store.ProposerBoostRoot == headRoot)
            throw new ForkChoiceException($"Cannot evaluate a proposer reorg for {headRoot}: it still holds the proposer boost");

        // The spec's get_weight is a live computation, not a cached one; settle deltas and the
        // (already-worn-off) boost before reading weights below.
        GetHead();

        bool headLate = IsHeadLate(headRoot);
        // No is_shuffling_stable: Fulu's proposer lookahead fixes the proposer before the epoch boundary (specs/fulu/fork-choice.md, EIP-7917).
        bool ffgCompetitive = _protoArray.GetUnrealizedJustifiedCheckpoint(headRoot) == _protoArray.GetUnrealizedJustifiedCheckpoint(parentRoot);
        bool finalizationOk = IsFinalizationOk(proposalSlot, _store.FinalizedCheckpoint.Epoch, ReorgMaxEpochsSinceFinalization);
        bool proposingOnTime = IsProposingOnTime();
        bool singleSlotReorg = IsSingleSlotReorg(parentSlot, headSlot, proposalSlot);

        JustifiedBalances justifiedBalances = GetJustifiedBalances(_store.JustifiedCheckpoint);
        ulong headWeight = _protoArray.GetWeight(headRoot) ?? throw new ForkChoiceException($"Block {headRoot} is unknown to fork choice");
        ulong parentWeight = _protoArray.GetWeight(parentRoot) ?? throw new ForkChoiceException($"Block {parentRoot} is unknown to fork choice");
        bool headWeak = headWeight < _protoArray.CalculateCommitteeFraction(justifiedBalances, ReorgHeadWeightThresholdPercent);
        bool parentStrong = parentWeight > _protoArray.CalculateCommitteeFraction(justifiedBalances, ReorgParentWeightThresholdPercent);

        return headLate && ffgCompetitive && finalizationOk && proposingOnTime && singleSlotReorg && headWeak && parentStrong;
    }

    /// <summary>The spec's <c>is_head_late</c>: a block with no recorded timeliness (unknown to this store) is treated as late, denying a reorg rather than allowing one on missing data.</summary>
    private bool IsHeadLate(Hash256 headRoot) => !_blockTimeliness.TryGetValue(headRoot, out bool timely) || !timely;

    /// <summary>The spec's <c>is_proposing_on_time</c>: whether the wall clock is still in the first half of the attesting interval.</summary>
    private bool IsProposingOnTime()
    {
        ulong timeIntoSlot = (Time - GenesisTime) % _spec.SecondsPerSlot;
        ulong cutoff = _spec.SecondsPerSlot / Presets.IntervalsPerSlot / 2;
        return timeIntoSlot <= cutoff;
    }

    /// <summary>
    /// The spec's <c>is_finalization_ok</c>: whether finality is recent enough, as of <paramref name="slot"/>,
    /// to permit a proposer reorg. <paramref name="finalizedEpoch"/> can never exceed <paramref name="slot"/>'s
    /// epoch in a consistent store, but if it somehow did, the ulong underflow yields a huge gap and this
    /// fails closed (no reorg) rather than wrapping into a false "ok".
    /// </summary>
    public static bool IsFinalizationOk(ulong slot, ulong finalizedEpoch, ulong reorgMaxEpochsSinceFinalization) =>
        BeaconStateAccessors.ComputeEpochAtSlot(slot) - finalizedEpoch <= reorgMaxEpochsSinceFinalization;

    /// <summary>The spec's single-slot-reorg guard: the parent must directly precede the head, and the head must directly precede the proposal slot.</summary>
    public static bool IsSingleSlotReorg(ulong parentSlot, ulong headSlot, ulong proposalSlot) =>
        parentSlot + 1 == headSlot && headSlot + 1 == proposalSlot;

    /// <summary>The spec's <c>get_checkpoint_block</c>: the ancestor of <paramref name="root"/> at the start of <paramref name="epoch"/>.</summary>
    private Hash256 GetCheckpointBlock(Hash256 root, ulong epoch) =>
        _protoArray.GetAncestor(root, BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch))
            ?? throw new ForkChoiceException($"Block {root} is unknown to fork choice");

    /// <summary>
    /// The spec's <c>store.block_states[root]</c>, typed by the fork of the block's own slot so that a
    /// Gloas root is never offered to the Fulu-typed <see cref="IForkChoiceStateProvider"/>.
    /// </summary>
    /// <exception cref="ForkChoiceException">The block is unknown, has no state, or is a Gloas block and this runner has no <see cref="IGloasBlockStateProvider"/>.</exception>
    private ForkedBeaconState GetBlockState(Hash256 blockRoot)
    {
        ulong slot = _protoArray.GetBlockSlot(blockRoot) ?? throw new ForkChoiceException($"Block {blockRoot} is unknown to fork choice");
        if (!IsGloasSlot(slot))
        {
            return new ForkedBeaconState.OfFulu(_stateProvider.GetBlockState(blockRoot)
                ?? throw new ForkChoiceException($"No state for the block {blockRoot}"));
        }

        IGloasBlockStateProvider provider = _gloasStateProvider
            ?? throw new ForkChoiceException($"Block {blockRoot} at slot {slot} is a Gloas block, but no {nameof(IGloasBlockStateProvider)} was supplied");
        return new ForkedBeaconState.OfGloas(provider.GetGloasBlockState(blockRoot)
            ?? throw new ForkChoiceException($"No state for the Gloas block {blockRoot}"));
    }

    /// <summary>The spec's <c>store_target_checkpoint_state</c>: the checkpoint's block state advanced to the checkpoint epoch start, cached.</summary>
    /// <remarks>
    /// A checkpoint at or after <see cref="BeaconChainSpec.GloasForkEpoch"/> whose block is a Fulu block
    /// (the epoch opened with skipped slots) is advanced to the fork boundary, upgraded, and then
    /// advanced under the Gloas slot processing, exactly as <see cref="ForkedStateTransition"/> carries a
    /// state across the fork. The block state itself is never mutated.
    /// </remarks>
    /// <exception cref="ForkChoiceException">The checkpoint block's state cannot be resolved; see <see cref="GetBlockState"/>.</exception>
    internal ForkedBeaconState GetCheckpointState(CheckpointRef checkpoint)
    {
        if (_checkpointStates.TryGetValue(checkpoint, out ForkedBeaconState? cached))
            return cached;

        ForkedBeaconState state = GetBlockState(checkpoint.Root);
        ulong startSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(checkpoint.Epoch);
        if (state.Slot < startSlot)
        {
            state = AdvanceCopy(checkpoint.Root, state, startSlot, IsGloasSlot(startSlot) ? BeaconFork.Gloas : BeaconFork.Fulu);
            // The epoch transitions above can apply pending deposits and grow the registry.
            ExtendPubkeys(ValidatorsOf(state));
        }

        _checkpointStates[checkpoint] = state;
        return state;
    }

    /// <summary>A mutable copy of <paramref name="blockState"/> advanced to <paramref name="targetSlot"/>, crossing into <paramref name="targetFork"/> on the way when needed.</summary>
    private ForkedBeaconState AdvanceCopy(Hash256 blockRoot, ForkedBeaconState blockState, ulong targetSlot, BeaconFork targetFork)
    {
        EpochCache cache = new();
        ForkedBeaconState state = blockState switch
        {
            ForkedBeaconState.OfFulu => new ForkedBeaconState.OfFulu(_stateProvider.CopyBlockState(blockRoot)!),
            ForkedBeaconState.OfGloas gloas => new ForkedBeaconState.OfGloas(gloas.State.Clone()),
            _ => throw new NotSupportedException($"Unhandled block state {blockState.GetType().Name}"),
        };

        state = ForkedStateTransition.CrossBoundaryIfNeeded(state, targetFork, _spec, cache);
        switch (state)
        {
            case ForkedBeaconState.OfFulu fulu:
                SlotProcessing.ProcessSlots(fulu.State, targetSlot, cache);
                break;
            // The crossing stops at the boundary slot, which may already be the target.
            case ForkedBeaconState.OfGloas gloas when gloas.Slot < targetSlot:
                GloasSlotProcessing.ProcessSlots(gloas.State, targetSlot, cache);
                break;
        }

        return state;
    }

    private static Validator[] ValidatorsOf(ForkedBeaconState state) => state switch
    {
        ForkedBeaconState.OfFulu fulu => fulu.State.Validators!,
        ForkedBeaconState.OfGloas gloas => gloas.State.Validators!,
        _ => throw new NotSupportedException($"Unhandled state {state.GetType().Name}"),
    };

    /// <summary>The spec's <c>get_weight</c> balance source: effective balances of the justified state's active, unslashed validators.</summary>
    private JustifiedBalances GetJustifiedBalances(CheckpointRef justified)
    {
        if (_justifiedBalances.TryGetValue(justified, out JustifiedBalances? cached))
            return cached;

        ForkedBeaconState state = GetCheckpointState(justified);
        ulong epoch = BeaconStateAccessors.ComputeEpochAtSlot(state.Slot);
        Validator[] validators = ValidatorsOf(state);
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

    private void ExtendPubkeys(Validator[] validators)
    {
        if (validators.Length > _pubkeys.Count)
            _pubkeys.Extend(validators, _pubkeys.Count);
    }
}
