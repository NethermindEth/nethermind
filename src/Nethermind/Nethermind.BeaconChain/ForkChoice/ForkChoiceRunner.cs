// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>
/// The spec-level fork-choice handlers (<c>on_tick</c>, <c>on_block</c>, <c>on_attestation</c>,
/// <c>on_attester_slashing</c>, <c>on_payload_attestation_message</c>, <c>get_head</c>) over the proto-array implementation, following
/// the consensus-specs fork-choice document (Electra/Fulu rules), with Gloas blocks registered through
/// their own <see cref="OnBlock(SignedBeaconBlockGloas, BeaconStateGloas)"/> overload.
/// </summary>
/// <remarks>
/// Owns the spec <c>Store</c> state that is not in the proto-array: wall-clock time, the realized
/// and unrealized checkpoints (via <see cref="ForkChoiceStore"/>), the proposer boost root, the verified payloads and PTC votes,
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

    /// <summary>A target checkpoint state per shuffling of the current and previous epochs, which votes for any target with that shuffling are checked against.</summary>
    private readonly Dictionary<ShufflingKey, ForkedBeaconState> _voteStates = [];

    /// <summary>The spec's <c>store.block_timeliness</c>: whether each block arrived before its slot's attestation and PTC deadlines, keyed by block root.</summary>
    private readonly Dictionary<Hash256, BlockTimeliness> _blockTimeliness = [];

    /// <summary>The spec's <c>store.payload_timeliness_vote</c> and <c>store.payload_data_availability_vote</c>, kept for every Gloas block.</summary>
    private readonly Dictionary<Hash256, PtcVotes> _ptcVotes = [];

    /// <summary>The spec's <c>store.payloads</c>, as roots only: the Gloas blocks whose execution payload envelope was delivered and verified.</summary>
    private readonly HashSet<Hash256> _payloads = [];

    /// <summary>The committed bid's <c>parent_block_hash</c> of each Gloas block, keyed by block root.</summary>
    private readonly Dictionary<Hash256, Hash256> _parentBlockHashes = [];

    /// <summary>specs/bellatrix/optimistic-sync.md: invalid Gloas payloads cannot become FULL again.</summary>
    private readonly HashSet<Hash256> _invalidPayloads = [];

    /// <summary>specs/bellatrix/optimistic-sync.md: a Gloas payload reported VALID cannot become INVALID.</summary>
    private readonly HashSet<Hash256> _validPayloads = [];

    /// <summary>The slot and proposer of each registered block, for <c>is_proposer_equivocation</c>; pruned by slot, and <see cref="_blockTimeliness"/> keeps the roots it holds, so <c>should_apply_proposer_boost</c> sees both for the same blocks.</summary>
    private readonly Dictionary<Hash256, BlockProposer> _blockProposers = [];

    /// <summary>Committee shufflings only; safe to share across forks (keyed by decision root). The balance memo is never used through this instance.</summary>
    private readonly EpochCache _committees = new();

    /// <summary>Makes the hasher of one checkpoint state advance, which serves every <c>process_slot</c> state root of that advance.</summary>
    /// <remarks>An incremental hasher pays one full merkleization per advance instead of one per skipped slot.</remarks>
    internal Func<IBeaconStateHasher> CheckpointStateHasher { get; set; } = static () => new CachedBeaconStateHasher();

    /// <summary>An attestation for the current slot, validated and indexed, waiting for the next slot tick (the spec only counts attestations from past slots).</summary>
    private readonly record struct QueuedAttestation(ulong Slot, ulong[] AttestingIndices, Hash256 BlockRoot, ulong TargetEpoch, bool? PayloadPresent);

    /// <summary>The fields an indexed attestation carries in both the Fulu and the Gloas container.</summary>
    private readonly record struct IndexedVote(ulong[] AttestingIndices, AttestationData Data, BlsSignature Signature);

    /// <summary>The fields a <c>SignedAggregateAndProof</c> adds around its aggregate, with <c>hash_tree_root</c> of the container it came in.</summary>
    private readonly record struct AggregatorProof(ulong AggregatorIndex, BlsSignature SelectionProof, Hash256 MessageRoot, BlsSignature Signature);

    private readonly record struct BlockProposer(ulong Slot, ulong ProposerIndex);

    /// <summary>
    /// The block that fixes an epoch's shuffling, as the proto-array sees it: <paramref name="Root"/> is that block, or, when
    /// <paramref name="BelowTreeRoot"/>, the tree root whose own ancestor it is, which every block the tree holds shares.
    /// </summary>
    private readonly record struct ShufflingKey(ulong Epoch, Hash256 Root, bool BelowTreeRoot);

    /// <summary>A <c>store.block_timeliness</c> entry: its <c>ATTESTATION_TIMELINESS_INDEX</c> and <c>PTC_TIMELINESS_INDEX</c> flags.</summary>
    private readonly record struct BlockTimeliness(bool Attestation, bool Ptc);

    /// <summary>The <c>on_payload_attestation_message</c> writes of one or more validators voting the same data, resolved before any is applied.</summary>
    private sealed record PtcVoteWrite(PtcVotes Votes, List<int> Seats, bool PayloadPresent, bool BlobDataAvailable)
    {
        public void Apply()
        {
            foreach (int seat in Seats)
            {
                Votes.Timeliness[seat] = PayloadPresent;
                Votes.DataAvailability[seat] = BlobDataAvailable;
            }
        }
    }

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
            slotsPerEpoch: spec.SlotsPerEpoch,
            isGloas: anchor.IsGloas);

        _blockProposers[anchor.Root] = new BlockProposer(anchor.BlockSlot, anchor.ProposerIndex);
        if (anchor.IsGloas)
        {
            // specs/gloas/fork-choice.md get_forkchoice_store: block_timeliness={anchor_root: [True, True]}, and [None] * PTC_SIZE votes (#5545).
            _blockTimeliness[anchor.Root] = new BlockTimeliness(Attestation: true, Ptc: true);
            _ptcVotes[anchor.Root] = new PtcVotes();
        }
    }

    /// <summary>The fork-independent parts of an anchor that <c>get_forkchoice_store</c> reads.</summary>
    private readonly record struct AnchorNode(ulong GenesisTime, ulong StateSlot, ulong Epoch, ulong BlockSlot, ulong ProposerIndex, Hash256 Root, Hash256 StateRoot, Hash256? ExecutionBlockHash, bool IsGloas);

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
            anchorBlock.ProposerIndex,
            SszRoots.HashTreeRoot(anchorBlock),
            anchorBlock.StateRoot!,
            anchorBlock.Body?.ExecutionPayload?.BlockHash,
            IsGloas: false);
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
            anchorBlock.ProposerIndex,
            SszRoots.HashTreeRoot(anchorBlock),
            anchorBlock.StateRoot!,
            anchorBlock.Body!.SignedExecutionPayloadBid!.Message!.BlockHash!,
            IsGloas: true);
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

    /// <summary>The execution status of <paramref name="blockRoot"/>'s payload, or <c>null</c> when the block is unknown.</summary>
    internal ExecutionStatus? GetBlockExecutionStatus(Hash256 blockRoot) => _protoArray.GetBlockExecutionStatus(blockRoot);

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
    /// before the finalized one, and the per-block records of every block the tree no longer holds
    /// (the proposer and timeliness records only of those at or before the finalized slot).
    /// </summary>
    public void Prune()
    {
        CheckpointRef finalized = _store.FinalizedCheckpoint;
        _protoArray.MaybePrune(finalized.Root);
        PruneCheckpointCache(_checkpointStates, finalized.Epoch);
        PruneCheckpointCache(_justifiedBalances, finalized.Epoch);
        PruneProposersAtOrBelowFinalizedSlot(BeaconStateAccessors.ComputeStartSlotAtEpoch(finalized.Epoch));
        PruneUnknownRoots(_blockTimeliness, _blockProposers.ContainsKey);
        PruneUnknownRoots(_parentBlockHashes);
        PruneUnknownRoots(_ptcVotes);
        _payloads.RemoveWhere(root => !_protoArray.ContainsBlock(root));
        _invalidPayloads.RemoveWhere(root => !_protoArray.ContainsBlock(root));
        _validPayloads.RemoveWhere(root => !_protoArray.ContainsBlock(root));
    }

    /// <summary>
    /// Drops the proposer records of blocks the tree no longer holds and whose slot is at or before the finalized slot. A block off
    /// the finalized chain keeps its record until finality passes its slot: the spec's unpruned <c>store.blocks</c> still counts it in
    /// <c>is_proposer_equivocation</c>.
    /// </summary>
    private void PruneProposersAtOrBelowFinalizedSlot(ulong finalizedSlot)
    {
        List<Hash256>? stale = null;
        foreach ((Hash256 root, BlockProposer proposer) in _blockProposers)
        {
            if (proposer.Slot <= finalizedSlot && !_protoArray.ContainsBlock(root)) (stale ??= []).Add(root);
        }

        if (stale is not null)
        {
            foreach (Hash256 root in stale) _blockProposers.Remove(root);
        }
    }

    private void PruneUnknownRoots<TValue>(Dictionary<Hash256, TValue> byRoot, Func<Hash256, bool>? isRetained = null)
    {
        List<Hash256>? stale = null;
        foreach (Hash256 root in byRoot.Keys)
        {
            if (!_protoArray.ContainsBlock(root) && isRetained?.Invoke(root) != true) (stale ??= []).Add(root);
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
        Hash256 blockRoot = SszRoots.HashTreeRoot(block);
        // specs/phase0/fork-choice.md on_block: return early if the block is already known.
        if (_protoArray.ContainsBlock(blockRoot))
            return;
        Hash256 parentRoot = block.ParentRoot!;
        ValidateOnBlock(block.Slot, parentRoot);

        if (!availability.IsDataAvailable(block, blockRoot, _spec))
            throw new ForkChoiceException($"Block {blockRoot} at slot {block.Slot} does not have all its blob data available");
        ExtendPubkeys(postState.Validators!);

        RegisterBlock(
            block.Slot,
            block.ProposerIndex,
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
    /// Its execution status is that of the payload the bid builds on; its own payload's verdicts are kept apart (<see cref="InvalidateExecutionChain"/>).
    /// There is no availability argument because the Gloas <c>on_block</c> no longer calls
    /// <c>is_data_available</c>. The body replay contract is the one the Fulu overload documents, through
    /// the <see cref="AttestationGloas"/> and <see cref="AttesterSlashingGloas"/> overloads.
    /// A block that builds on its parent's full payload (<see cref="IsParentNodeFull"/>) is refused until
    /// that payload is recorded through <see cref="OnExecutionPayloadVerified"/>; the refusal leaves the store untouched.
    /// The body's payload attestations are applied here, as the spec's <c>notify_ptc_messages</c> does, not by the caller:
    /// each is resolved against the store before the block is registered and written only once it is.
    /// </remarks>
    /// <exception cref="ForkChoiceException">
    /// The block's slot is before the Gloas fork, this runner has no <see cref="IGloasBlockStateProvider"/>,
    /// or the block violates an <c>on_block</c> assertion, including a full parent whose payload is not verified
    /// and a body payload attestation that <see cref="OnPayloadAttestationMessage"/> refuses.
    /// </exception>
    /// <exception cref="BeaconStateException">A body payload attestation's bits do not match its slot's PTC in <paramref name="postState"/>.</exception>
    public void OnBlock(SignedBeaconBlockGloas signedBlock, BeaconStateGloas postState)
    {
        BeaconBlockGloas block = signedBlock.Message!;
        if (!IsGloasSlot(block.Slot))
            throw new ForkChoiceException($"Block at slot {block.Slot} is before the Gloas fork; it must be a {nameof(SignedBeaconBlock)}");
        if (_gloasStateProvider is null)
            throw new ForkChoiceException($"Block at slot {block.Slot} is a Gloas block, but no {nameof(IGloasBlockStateProvider)} was supplied to resolve its checkpoint states");
        Hash256 blockRoot = SszRoots.HashTreeRoot(block);
        // specs/gloas/fork-choice.md on_block: return early if the block is already known.
        if (_protoArray.ContainsBlock(blockRoot))
            return;
        Hash256 parentRoot = block.ParentRoot!;
        ValidateOnBlock(block.Slot, parentRoot);
        // specs/gloas/fork-choice.md on_block: if is_parent_node_full, assert is_payload_verified(parent_root).
        if (IsParentNodeFull(block) && !IsPayloadVerified(parentRoot))
            throw new ForkChoiceException($"Block at slot {block.Slot} builds on the full payload of {parentRoot}, which is not verified");

        List<PtcVoteWrite> blockPtcVotes = [];
        foreach (PayloadAttestation attestation in block.Body!.PayloadAttestations ?? [])
        {
            IndexedPayloadAttestation indexed = postState.GetIndexedPayloadAttestation(attestation, _spec);
            if (ResolvePtcVote(attestation.Data!, indexed.AttestingIndices!, default, isFromBlock: true, verifySignature: false) is { } write)
                blockPtcVotes.Add(write);
        }

        ExtendPubkeys(postState.Validators!);

        ExecutionPayloadBid bid = block.Body!.SignedExecutionPayloadBid!.Message!;
        RegisterBlock(
            block.Slot,
            block.ProposerIndex,
            blockRoot,
            parentRoot,
            block.StateRoot!,
            CheckpointRef.From(postState.CurrentJustifiedCheckpoint!),
            CheckpointRef.From(postState.FinalizedCheckpoint!),
            GloasEpochProcessing.ComputeJustificationAndFinalization(postState, new EpochCache()),
            ExecutionStatus.Optimistic,
            bid.BlockHash!,
            isGloas: true,
            parentBlockHash: bid.ParentBlockHash);
        _parentBlockHashes[blockRoot] = bid.ParentBlockHash!;
        _ptcVotes[blockRoot] = new PtcVotes();
        foreach (PtcVoteWrite write in blockPtcVotes)
            write.Apply();
    }

    /// <summary>
    /// The spec's <c>on_payload_attestation_message</c>: records a PTC member's vote on whether the payload of
    /// <c>data.beacon_block_root</c> arrived in time and its blob data is available, at every seat the member holds.
    /// </summary>
    /// <remarks>
    /// A vote whose <c>data.slot</c> is not its block's slot is ignored without error, as the spec returns early. The votes
    /// feed the head's EMPTY-or-FULL choice for a block of the previous slot (<see cref="GetHeadNode"/>). A block's own
    /// payload attestations reach the store through the Gloas <see cref="OnBlock(SignedBeaconBlockGloas, BeaconStateGloas)"/>,
    /// which passes <paramref name="isFromBlock"/> for each attester.
    /// </remarks>
    /// <param name="isFromBlock">Whether the vote came in a block body, which skips the current-slot and signature checks.</param>
    /// <param name="verifySignature">Skippable for a gossip message whose signature the caller already verified.</param>
    /// <exception cref="ForkChoiceException">
    /// The message has no data or block root, the block is unknown or pre-Gloas, the validator is not in the slot's PTC, or a
    /// gossip vote is not for the current slot or fails <c>is_valid_indexed_payload_attestation</c>. Nothing is written.
    /// </exception>
    public void OnPayloadAttestationMessage(PayloadAttestationMessage message, bool isFromBlock = false, bool verifySignature = true) =>
        ResolvePtcVote(
            message.Data ?? throw new ForkChoiceException($"Payload attestation from validator {message.ValidatorIndex} has no data"),
            [message.ValidatorIndex], message.Signature, isFromBlock, verifySignature)?.Apply();

    /// <summary>
    /// Every assertion of <c>on_payload_attestation_message</c> for <paramref name="validatorIndices"/> voting <paramref name="data"/>,
    /// and the seats they hold; <see langword="null"/> when the spec returns early and writes nothing.
    /// </summary>
    private PtcVoteWrite? ResolvePtcVote(PayloadAttestationData data, ulong[] validatorIndices, BlsSignature signature, bool isFromBlock, bool verifySignature)
    {
        Hash256 root = data.BeaconBlockRoot ?? throw new ForkChoiceException($"Payload attestation at slot {data.Slot} has no block root");
        ForkedBeaconState state = GetBlockState(root);
        if (data.Slot != state.Slot)
            return null;
        // get_ptc asserts a Gloas epoch, and only Gloas blocks have PTC votes.
        if (state is not ForkedBeaconState.OfGloas { State: BeaconStateGloas gloasState } || !_ptcVotes.TryGetValue(root, out PtcVotes? votes))
            throw new ForkChoiceException($"Payload attestation for {root} at slot {data.Slot}: the block is before the Gloas fork and has no PTC");

        HashSet<ulong> voters = [.. validatorIndices];
        HashSet<ulong> seated = [];
        List<int> seats = [];
        ulong[] ptc = gloasState.GetPtc(data.Slot, _spec).Indices!;
        for (int seat = 0; seat < ptc.Length; seat++)
        {
            if (voters.Contains(ptc[seat]))
            {
                seats.Add(seat);
                seated.Add(ptc[seat]);
            }
        }

        if (seated.Count != voters.Count)
            throw new ForkChoiceException($"Payload attestation for {root} at slot {data.Slot} has a validator outside that slot's PTC");

        if (!isFromBlock)
        {
            if (data.Slot != _store.CurrentSlot)
                throw new ForkChoiceException($"Payload attestation for slot {data.Slot} is not for the current slot {_store.CurrentSlot}");
            IndexedPayloadAttestation indexed = new() { AttestingIndices = validatorIndices, Data = data, Signature = signature };
            if (!GloasBlockProcessing.IsValidIndexedPayloadAttestation(gloasState, indexed, _pubkeys, verifySignature))
                throw new ForkChoiceException($"Payload attestation for {root} at slot {data.Slot} has invalid indices or signature");
        }

        return new PtcVoteWrite(votes, seats, data.PayloadPresent, data.BlobDataAvailable);
    }

    /// <summary>The PTC votes kept for <paramref name="blockRoot"/>, or <see langword="null"/> for a pre-Gloas or unknown block.</summary>
    internal (IReadOnlyList<bool?> Timeliness, IReadOnlyList<bool?> DataAvailability)? GetPtcVotes(Hash256 blockRoot) =>
        _ptcVotes.TryGetValue(blockRoot, out PtcVotes? votes) ? (votes.Timeliness, votes.DataAvailability) : null;

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
        if (!_invalidPayloads.Contains(blockRoot))
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
    /// <param name="isGloas">Whether the block carries a signed execution payload bid, whose <c>block_hash</c> is then <paramref name="executionBlockHash"/>.</param>
    /// <param name="parentBlockHash">The bid's <c>parent_block_hash</c>, which decides whether the block builds on its parent's EMPTY or FULL node.</param>
    private void RegisterBlock(
        ulong slot,
        ulong proposerIndex,
        Hash256 blockRoot,
        Hash256 parentRoot,
        Hash256 stateRoot,
        CheckpointRef stateJustified,
        CheckpointRef stateFinalized,
        JustificationAndFinalizationState pulledUp,
        ExecutionStatus executionStatus,
        Hash256? executionBlockHash,
        bool isGloas = false,
        Hash256? parentBlockHash = null)
    {
        // Checked before the first store update: the proto-array would refuse it only after the boost and checkpoints moved.
        if ((executionStatus == ExecutionStatus.Irrelevant) != (executionBlockHash is null))
            throw new ForkChoiceException($"Block {blockRoot} must carry an execution block hash if and only if execution is enabled");

        // A valid post-state never names a checkpoint epoch after its block's own; a huge one would stall get_head or freeze finality.
        ulong blockEpoch = BeaconStateAccessors.ComputeEpochAtSlot(slot);
        foreach (ulong epoch in (ReadOnlySpan<ulong>)[stateJustified.Epoch, stateFinalized.Epoch, pulledUp.CurrentJustifiedCheckpoint.Epoch, pulledUp.FinalizedCheckpoint.Epoch])
        {
            if (epoch > blockEpoch)
                throw new ForkChoiceException($"Block {blockRoot} names checkpoint epoch {epoch}, after its own epoch {blockEpoch}");
        }

        // Proposer boost for the first block of the slot arriving before get_attestation_due_ms, which Gloas moves earlier
        // (specs/phase0/fork-choice.md and specs/gloas/fork-choice.md record_block_timeliness).
        ulong slotDurationMs = _spec.SecondsPerSlot * 1000;
        ulong secondsSinceGenesis = Time - GenesisTime;
        ulong timeIntoSlotMs = (secondsSinceGenesis > ulong.MaxValue / 1000 ? ulong.MaxValue : secondsSinceGenesis * 1000) % slotDurationMs;
        ulong attestationDueMs = (IsGloasSlot(slot) ? GloasTiming.AttestationDueBpsGloas : GloasTiming.AttestationDueBps) * slotDurationMs / Presets.BasisPoints;
        ulong ptcDueMs = GloasTiming.PayloadAttestationDueBps * slotDurationMs / Presets.BasisPoints;
        bool isCurrentSlot = slot == _store.CurrentSlot;
        BlockTimeliness timeliness = new(isCurrentSlot && timeIntoSlotMs < attestationDueMs, isCurrentSlot && timeIntoSlotMs < ptcDueMs);
        bool isTimely = timeliness.Attestation;
        // update_proposer_boost_root reads the pre-block get_head only for a timely first block of the slot.
        bool isBoosted = isTimely && _store.ProposerBoostRoot == Hash256.Zero && HasHeadDependentRoot(parentRoot);

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
                UnrealizedFinalizedCheckpoint: unrealizedFinalized,
                IsGloas: isGloas,
                ParentBlockHash: parentBlockHash),
            _store.CurrentSlot,
            _store.JustifiedCheckpoint,
            _store.FinalizedCheckpoint);

        // Recorded only once ProcessBlock returns: a block it refuses must neither hold the slot's boost nor count as its proposal.
        _blockTimeliness[blockRoot] = timeliness;
        if (isBoosted)
            _store.ProposerBoostRoot = blockRoot;
        _blockProposers[blockRoot] = new BlockProposer(slot, proposerIndex);
    }

    /// <summary>
    /// The <c>is_same_dependent_root</c> term of <c>update_proposer_boost_root</c> (specs/phase0/fork-choice.md, unchanged
    /// by specs/gloas/fork-choice.md apart from the payload status of the walk): whether a current-slot block on
    /// <paramref name="parentRoot"/> and the pre-block <c>get_head</c> share <c>get_shuffling_dependent_root</c> at the store epoch.
    /// </summary>
    /// <remarks>
    /// Must run before the block is added and before the checkpoints move. A current-slot block is always after the
    /// dependent slot, so its dependent root is its parent's. A pruned walk ends <see langword="null"/> for both roots at
    /// once: both descend from the finalized root, below which the spec's walk reaches the same block.
    /// </remarks>
    private bool HasHeadDependentRoot(Hash256 parentRoot)
    {
        ulong lookaheadStartSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(_store.CurrentEpoch > Presets.MinSeedLookahead ? _store.CurrentEpoch - Presets.MinSeedLookahead : 0);
        ulong dependentSlot = lookaheadStartSlot > 0 ? lookaheadStartSlot - 1 : 0;
        return _protoArray.GetAncestor(GetHead(), dependentSlot) == _protoArray.GetAncestor(parentRoot, dependentSlot);
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

    /// <summary>Applies an aggregate after authenticating its wrapper under p2p-interface.md beacon_aggregate_and_proof.</summary>
    /// <remarks>The target checkpoint state supplies the vote's committee and domains; all three signatures verify together.</remarks>
    internal void OnAggregateAndProof(SignedAggregateAndProof signed)
    {
        AggregateAndProof message = signed.Message!;
        Attestation aggregate = message.Aggregate!;
        OnAttestation(aggregate.Data!, aggregate.AggregationBits!, aggregate.CommitteeBits!, aggregate.Signature, isFromBlock: false, verifySignature: true, gloasContainer: false,
            new AggregatorProof(message.AggregatorIndex, message.SelectionProof, SszRoots.HashTreeRoot(message), signed.Signature));
    }

    /// <inheritdoc cref="OnAggregateAndProof(SignedAggregateAndProof)"/>
    internal void OnAggregateAndProof(SignedAggregateAndProofGloas signed)
    {
        AggregateAndProofGloas message = signed.Message!;
        AttestationGloas aggregate = message.Aggregate!;
        OnAttestation(aggregate.Data!, aggregate.AggregationBits!, aggregate.CommitteeBits!, aggregate.Signature, isFromBlock: false, verifySignature: true, gloasContainer: true,
            new AggregatorProof(message.AggregatorIndex, message.SelectionProof, SszRoots.HashTreeRoot(message), signed.Signature));
    }

    private void OnAttestation(AttestationData data, BitArray aggregationBits, BitArray committeeBits, BlsSignature signature, bool isFromBlock, bool verifySignature, bool gloasContainer,
        AggregatorProof? aggregator = null)
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

        ForkedBeaconState targetState = GetVoteTargetState(target, out bool unheld);
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
        IndexedVote vote = new(attestingIndices, data, signature);
        if (aggregator is { } proof)
            VerifyAggregator(targetState, target.Epoch, data.Slot, committeeBits, proof, vote);
        else if (!IsValidIndexedAttestation(targetState, vote, verifySignature))
            throw new ForkChoiceException("Attestation indices or aggregate signature are invalid");

        // Cached only once the vote verified, so a refused one leaves no checkpoint state behind.
        if (unheld)
            _checkpointStates[target] = targetState;

        // specs/gloas/fork-choice.md update_latest_messages: payload_present = data.index == 1; a pre-Gloas slot has no payload vote.
        bool? payloadPresent = IsGloasSlot(data.Slot) ? data.Index == 1 : null;

        // Attestations can only affect the fork choice of subsequent slots; current-slot
        // attestations wait in the queue until the next tick.
        if (!isFromBlock && data.Slot == _store.CurrentSlot)
        {
            _queuedAttestations.Add(new QueuedAttestation(data.Slot, attestingIndices, beaconBlockRoot, target.Epoch, payloadPresent));
            return;
        }

        ApplyVotes(attestingIndices, beaconBlockRoot, data.Slot, target.Epoch, payloadPresent);
    }

    // p2p-interface.md beacon_aggregate_and_proof: authenticate committee membership and all three signatures before applying votes.
    private void VerifyAggregator(ForkedBeaconState state, ulong targetEpoch, ulong slot, BitArray committeeBits, AggregatorProof proof, IndexedVote vote)
    {
        // get_attesting_indices has already refused committee bits naming no committee or one out of range.
        int committeeIndex = 0;
        while (!committeeBits[committeeIndex])
            committeeIndex++;

        CommitteeCache committees = state switch
        {
            ForkedBeaconState.OfFulu fulu => _committees.GetCommitteeCache(fulu.State, targetEpoch),
            ForkedBeaconState.OfGloas gloas => _committees.GetCommitteeCache(gloas.State, targetEpoch),
            _ => throw new NotSupportedException($"Unhandled state {state.GetType().Name}"),
        };
        ReadOnlySpan<int> committee = committees.GetBeaconCommittee(slot, committeeIndex);
        if (!BeaconStateAccessors.IsAggregator(committee.Length, proof.SelectionProof))
            throw new ForkChoiceException($"Validator {proof.AggregatorIndex} is not selected as an aggregator for slot {slot}");
        if (proof.AggregatorIndex > int.MaxValue || !committee.Contains((int)proof.AggregatorIndex))
            throw new ForkChoiceException($"Aggregator {proof.AggregatorIndex} is not a member of committee {committeeIndex} at slot {slot}");
        if (!SignatureSets.TryGetValidatorKey(_pubkeys, proof.AggregatorIndex, out G1Affine key))
            throw new ForkChoiceException($"Aggregator {proof.AggregatorIndex} has no valid public key");

        // compute_signing_root(slot, domain): hash_tree_root of a uint64 is its little-endian chunk.
        Span<byte> slotRoot = stackalloc byte[32];
        slotRoot.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(slotRoot, slot);
        Hash256 selectionRoot = Domains.ComputeSigningRoot(new Hash256(slotRoot), GetDomain(state, DomainType.SelectionProof, targetEpoch));
        Hash256 aggregatorRoot = Domains.ComputeSigningRoot(proof.MessageRoot, GetDomain(state, DomainType.AggregateAndProof, targetEpoch));

        BlockSignatureBatch batch = new();
        if (!BlockSignatureBatch.Verify(key, proof.SelectionProof, selectionRoot, batch.Defer("Aggregate selection proof is invalid"))
            || !BlockSignatureBatch.Verify(key, proof.Signature, aggregatorRoot, batch.Defer("Aggregator signature is invalid"))
            || !IsValidIndexedAttestation(state, vote, verifySignature: true, batch.Defer("Aggregate signature is invalid")))
            throw new ForkChoiceException("Aggregate indices or signatures are invalid");

        batch.Verify();
    }

    private static Hash256 GetDomain(ForkedBeaconState state, ReadOnlySpan<byte> domainType, ulong epoch) => state switch
    {
        ForkedBeaconState.OfFulu fulu => fulu.State.GetDomain(domainType, epoch),
        ForkedBeaconState.OfGloas gloas => gloas.State.GetDomain(domainType, epoch),
        _ => throw new NotSupportedException($"Unhandled state {state.GetType().Name}"),
    };

    /// <summary>
    /// The Gloas <c>validate_on_attestation</c> rules on <c>data.index</c>, which votes for the head block's
    /// payload status (specs/gloas/fork-choice.md, EIP-7732): 0 or 1, 0 for a vote in the block's own slot,
    /// and 1 only for a block whose payload is verified.
    /// </summary>
    /// <remarks>
    /// The last rule is the spec's literal <c>is_payload_verified</c>, <c>root in store.payloads</c>, so an index-1 vote for a
    /// pre-Gloas block is refused: its payload has no envelope. <see cref="IsPayloadVerified"/> exempts such a block for
    /// <c>on_block</c> only, where the literal check would refuse every first Gloas block.
    /// </remarks>
    private void ValidatePayloadStatusVote(AttestationData data, ulong blockSlot)
    {
        if (data.Index > 1)
            throw new ForkChoiceException($"Attestation index {data.Index} is not a payload status (0 or 1)");
        if (blockSlot == data.Slot && data.Index != 0)
            throw new ForkChoiceException($"Attestation for slot {data.Slot} votes for the payload of a block from its own slot");
        if (data.Index == 1 && !_payloads.Contains(data.BeaconBlockRoot!))
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
    internal static void ThrowIfOverGloasIndexedAttestationBound(ulong[]? attestingIndices, string what)
    {
        const int MaxAttestingIndices = Presets.MaxValidatorsPerCommittee * Presets.MaxCommitteesPerSlot;
        int count = attestingIndices?.Length ?? 0;
        if (count > MaxAttestingIndices)
            throw new ForkChoiceException($"{what} has {count} attesting indices, over the bound of {MaxAttestingIndices}");
    }

    /// <summary>The spec's <c>is_valid_indexed_attestation</c> of <paramref name="state"/>'s fork, over the vote in that fork's container.</summary>
    private bool IsValidIndexedAttestation(ForkedBeaconState state, IndexedVote vote, bool verifySignature, BlockSignatureBatch.Deferral? deferral = null) => state switch
    {
        ForkedBeaconState.OfFulu fulu => BlockProcessing.IsValidIndexedAttestation(
            fulu.State,
            new IndexedAttestation { AttestingIndices = vote.AttestingIndices, Data = vote.Data, Signature = vote.Signature },
            _pubkeys,
            verifySignature,
            deferral),
        ForkedBeaconState.OfGloas gloas => GloasBlockProcessing.IsValidIndexedAttestation(
            gloas.State,
            new IndexedAttestationGloas { AttestingIndices = vote.AttestingIndices, Data = vote.Data, Signature = vote.Signature },
            _pubkeys,
            verifySignature,
            deferral),
        _ => throw new NotSupportedException($"Unhandled state {state.GetType().Name}"),
    };

    /// <summary>The spec's <c>get_head</c>: LMD-GHOST from the justified checkpoint, weighted by the justified state's balances and the proposer boost.</summary>
    /// <returns>The head block's root; <see cref="GetHeadNode"/> also says whether its payload is part of the head.</returns>
    /// <exception cref="ForkChoiceException">The justified block has an invalid execution payload.</exception>
    public Hash256 GetHead() => GetHeadNode().Root;

    /// <summary>
    /// The spec's <c>get_head</c> as a <see cref="ForkChoiceNode"/>: the head block, and whether the head builds on its
    /// payload (<see cref="ForkChoicePayloadStatus.Full"/>) or on the payload before it (<see cref="ForkChoicePayloadStatus.Empty"/>).
    /// </summary>
    /// <remarks>
    /// While the store's current slot is before the Gloas fork this is the Fulu <c>get_head</c>. From the fork on it is the Gloas
    /// walk (specs/gloas/fork-choice.md <c>get_head</c>, <c>get_node_children</c>, <c>get_weight</c>,
    /// <c>get_payload_status_tiebreaker</c>), which never returns a PENDING node. A pre-Gloas head is EMPTY on both sides of the
    /// fork: it is never in <c>store.payloads</c>, so <c>get_node_children</c> gives it no FULL node, and <c>validate_on_attestation</c>
    /// refuses an index-1 vote for it. The walk still passes through a pre-Gloas block to the Gloas children that build on its payload.
    /// </remarks>
    /// <exception cref="ForkChoiceException">The justified block has an invalid execution payload.</exception>
    public ForkChoiceNode GetHeadNode()
    {
        // specs/bellatrix/optimistic-sync.md: an INVALIDATED justified checkpoint leaves no valid head; a node MAY exit.
        if (_protoArray.GetBlockExecutionStatus(_store.JustifiedCheckpoint.Root) == ExecutionStatus.Invalid)
            throw new ForkChoiceException($"Justified block {_store.JustifiedCheckpoint.Root} has an invalid execution payload");
        JustifiedBalances balances = GetJustifiedBalances(_store.JustifiedCheckpoint);
        _protoArray.SetProposerBoostRoot(_store.ProposerBoostRoot);
        Hash256 head = _protoArray.GetHead(_store.JustifiedCheckpoint, _store.FinalizedCheckpoint, balances, _equivocatingIndices, _store.CurrentSlot);
        return IsGloasSlot(_store.CurrentSlot) ? FindGloasHead(balances) : new ForkChoiceNode(head, ForkChoicePayloadStatus.Empty);
    }

    /// <summary>The Gloas <c>get_weight</c> of <paramref name="node"/>, after the score update of a <see cref="GetHeadNode"/> call.</summary>
    /// <exception cref="ForkChoiceException">The block is unknown, or <see cref="GetHeadNode"/> throws.</exception>
    internal ulong GetWeight(ForkChoiceNode node)
    {
        GetHeadNode();
        int index = _protoArray.IndexOf(node.Root) ?? throw new ForkChoiceException($"Block {node.Root} is unknown to fork choice");
        return new GloasWeights(this, GetJustifiedBalances(_store.JustifiedCheckpoint)).Of(index, node.PayloadStatus);
    }

    /// <summary>The Gloas <c>get_head</c> walk from (justified root, PENDING) over the weights the last score update left.</summary>
    private ForkChoiceNode FindGloasHead(JustifiedBalances balances)
    {
        IReadOnlyList<ProtoNode> nodes = _protoArray.Nodes;
        bool[] filtered = _protoArray.FilterBlockTree(_store.CurrentSlot, _store.JustifiedCheckpoint, _store.FinalizedCheckpoint);
        GloasWeights weights = new(this, balances);
        int index = _protoArray.IndexOf(_store.JustifiedCheckpoint.Root)
            ?? throw new ForkChoiceException($"Justified block {_store.JustifiedCheckpoint.Root} is unknown to fork choice");

        while (true)
        {
            ProtoNode node = nodes[index];
            ForkChoicePayloadStatus status = ChoosePayloadNode(node, index, weights);

            int? best = null;
            ulong bestWeight = 0;
            foreach (int child in node.Children)
            {
                ProtoNode childNode = nodes[child];
                if (!filtered[child] || childNode.ParentPayloadStatus != status)
                    continue;

                // Children are PENDING nodes of distinct roots, so (weight, root) decides and the payload tiebreaker never does.
                ulong weight = weights.Of(child, ForkChoicePayloadStatus.Pending);
                if (best is not int current || weight > bestWeight || (weight == bestWeight && childNode.Root.CompareTo(nodes[current].Root) > 0))
                {
                    best = child;
                    bestWeight = weight;
                }
            }

            if (best is not int next)
                return new ForkChoiceNode(node.Root, node.IsGloas ? status : ForkChoicePayloadStatus.Empty);
            index = next;
        }
    }

    /// <summary>
    /// The child of (<paramref name="node"/>, PENDING) that <c>get_head</c> takes: EMPTY, or FULL when the payload is verified and
    /// FULL wins on weight, then on <c>get_payload_status_tiebreaker</c> (the roots are equal).
    /// </summary>
    private ForkChoicePayloadStatus ChoosePayloadNode(ProtoNode node, int index, GloasWeights weights)
    {
        // Gloas children of a pre-Gloas block build on the payload it carried (ProtoArray.GetParentPayloadStatus).
        if (!node.IsGloas)
            return ForkChoicePayloadStatus.Full;
        // specs/gloas/fork-choice.md get_node_children: the FULL node exists only once is_payload_verified.
        if (!_payloads.Contains(node.Root))
            return ForkChoicePayloadStatus.Empty;

        ulong empty = weights.Of(index, ForkChoicePayloadStatus.Empty);
        ulong full = weights.Of(index, ForkChoicePayloadStatus.Full);
        if (empty != full)
            return full > empty ? ForkChoicePayloadStatus.Full : ForkChoicePayloadStatus.Empty;
        return GetPayloadStatusTiebreaker(node, ForkChoicePayloadStatus.Full) > GetPayloadStatusTiebreaker(node, ForkChoicePayloadStatus.Empty)
            ? ForkChoicePayloadStatus.Full
            : ForkChoicePayloadStatus.Empty;
    }

    /// <summary>The spec's <c>is_previous_slot_payload_decision</c>: an EMPTY or FULL node of a block from the slot before the current one.</summary>
    private bool IsPreviousSlotPayloadDecision(ulong blockSlot, ForkChoicePayloadStatus status) =>
        status != ForkChoicePayloadStatus.Pending && blockSlot + 1 == _store.CurrentSlot;

    /// <summary>The spec's <c>get_payload_status_tiebreaker</c>.</summary>
    private byte GetPayloadStatusTiebreaker(ProtoNode node, ForkChoicePayloadStatus status)
    {
        if (!IsPreviousSlotPayloadDecision(node.Slot, status))
            return (byte)status;
        if (status == ForkChoicePayloadStatus.Empty)
            return 1;
        return ShouldExtendPayload(node.Root) ? (byte)2 : (byte)0;
    }

    /// <summary>
    /// The spec's <c>should_extend_payload</c> for a block of the previous slot: extend its verified payload when the PTC voted it
    /// timely and its data available, or unless the boosted block builds on this block's EMPTY node.
    /// </summary>
    private bool ShouldExtendPayload(Hash256 root)
    {
        if (!_payloads.Contains(root))
            return false;
        PtcVotes votes = _ptcVotes[root];
        if (PtcVotes.HasQuorum(votes.Timeliness, true) && PtcVotes.HasQuorum(votes.DataAvailability, true))
            return true;

        Hash256 boostRoot = _store.ProposerBoostRoot;
        if (boostRoot == Hash256.Zero)
            return true;
        ProtoNode boost = _protoArray.Nodes[_protoArray.IndexOf(boostRoot) ?? throw new ForkChoiceException($"Proposer boost root {boostRoot} is unknown to fork choice")];
        return _protoArray.GetParentRoot(boostRoot) != root || boost.ParentPayloadStatus == ForkChoicePayloadStatus.Full;
    }

    /// <summary>
    /// The spec's <c>should_apply_proposer_boost</c>: the boost counts unless the boosted block's parent is from the previous slot, is
    /// weak, and its proposer had another block in that slot that arrived before the PTC deadline.
    /// </summary>
    /// <remarks>Valid only right after the score update of a <see cref="GetHeadNode"/> call, which <see cref="IsHeadWeak"/> reads.</remarks>
    private bool ShouldApplyProposerBoost(JustifiedBalances balances)
    {
        Hash256 boostRoot = _store.ProposerBoostRoot;
        if (boostRoot == Hash256.Zero)
            return false;

        ulong slot = _protoArray.GetBlockSlot(boostRoot) ?? throw new ForkChoiceException($"Proposer boost root {boostRoot} is unknown to fork choice");
        Hash256 parentRoot = _protoArray.GetParentRoot(boostRoot) ?? throw new ForkChoiceException($"Parent of the proposer boost root {boostRoot} is unknown to fork choice");
        ulong parentSlot = _protoArray.GetBlockSlot(parentRoot)!.Value;
        if (parentSlot + 1 < slot)
            return true;
        if (!IsHeadWeak(parentRoot, parentSlot, balances))
            return true;

        ulong parentProposer = _blockProposers.TryGetValue(parentRoot, out BlockProposer parent)
            ? parent.ProposerIndex
            : throw new ForkChoiceException($"Parent {parentRoot} of the proposer boost root has no recorded proposer");
        foreach ((Hash256 root, BlockProposer proposer) in _blockProposers)
        {
            bool ptcTimely = _blockTimeliness.TryGetValue(root, out BlockTimeliness timeliness) && timeliness.Ptc;
            if (ptcTimely && proposer.ProposerIndex == parentProposer && proposer.Slot + 1 == slot && root != parentRoot)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The spec's <c>get_weight</c> over the proto-array's per-node weights, which carry the proposer score on every node the
    /// boosted block descends from whenever it is known and valid (<see cref="ProtoArray.ApplyScoreChanges"/>).
    /// </summary>
    /// <remarks>
    /// The score is taken back off those nodes when <c>should_apply_proposer_boost</c> is false, and an EMPTY or FULL node of a
    /// previous-slot block weighs zero. The boosted path is the boosted block's PENDING node and, for each ancestor, its PENDING node
    /// and the payload node the path runs through (the child's <see cref="ProtoNode.ParentPayloadStatus"/>), per <c>is_ancestor</c>.
    /// </remarks>
    private sealed class GloasWeights
    {
        private readonly ForkChoiceRunner _runner;
        private readonly Dictionary<int, ForkChoicePayloadStatus?> _boostPath = [];
        private readonly ulong _unappliedBoost;

        public GloasWeights(ForkChoiceRunner runner, JustifiedBalances balances)
        {
            _runner = runner;
            ProtoArrayForkChoice protoArray = runner._protoArray;
            Hash256 boostRoot = runner._store.ProposerBoostRoot;
            if (boostRoot == Hash256.Zero || protoArray.IndexOf(boostRoot) is not int index)
                return;

            IReadOnlyList<ProtoNode> nodes = protoArray.Nodes;
            ProtoNode node = nodes[index];
            if (node.ExecutionStatus == ExecutionStatus.Invalid || runner.ShouldApplyProposerBoost(balances))
                return;

            // Mirrors where ProtoArray.ApplyProposerBoost added the score.
            _unappliedBoost = protoArray.CalculateCommitteeFraction(balances, ProtoArrayForkChoice.DefaultProposerScoreBoostPercent);
            _boostPath[index] = null;
            while (node.Parent is int parentIndex)
            {
                ProtoNode parent = nodes[parentIndex];
                if (parent.Root == Hash256.Zero || parent.ExecutionStatus == ExecutionStatus.Invalid)
                    break;
                _boostPath[parentIndex] = node.ParentPayloadStatus;
                node = parent;
            }
        }

        public ulong Of(int index, ForkChoicePayloadStatus status)
        {
            ProtoNode node = _runner._protoArray.Nodes[index];
            if (_runner.IsPreviousSlotPayloadDecision(node.Slot, status))
                return 0;

            ulong weight = status switch
            {
                ForkChoicePayloadStatus.Empty => node.EmptyWeight,
                ForkChoicePayloadStatus.Full => node.FullWeight,
                _ => node.Weight,
            };
            bool onBoostPath = _boostPath.TryGetValue(index, out ForkChoicePayloadStatus? pathStatus)
                && (status == ForkChoicePayloadStatus.Pending || pathStatus == status);
            return onBoostPath ? checked(weight - _unappliedBoost) : weight;
        }
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
    /// Applies an engine INVALID verdict on the payload <paramref name="payloadHash"/> of the chain ending at <paramref name="chainRoot"/>:
    /// that payload, and every payload after the one <paramref name="latestValidHash"/> names, becomes invalid with the blocks built on it.
    /// </summary>
    /// <remarks>
    /// specs/bellatrix/optimistic-sync.md: invalidate the execution suffix after latestValidHash; unknown hashes count as null.
    /// specs/gloas/fork-choice.md get_node_children: an invalid payload removes only FULL and blocks built on it, preserving EMPTY.
    /// </remarks>
    /// <param name="chainRoot">The last known beacon block on the execution chain being invalidated.</param>
    /// <param name="payloadHash">The payload in question; <c>null</c> when its block is not in fork choice and builds on <paramref name="chainRoot"/>'s payload.</param>
    /// <param name="latestValidHash">The engine's latest valid execution hash, or <c>null</c> to invalidate only the payload in question.</param>
    /// <exception cref="ProtoArrayException">A payload to invalidate was reported valid before.</exception>
    public void InvalidateExecutionChain(Hash256 chainRoot, Hash256? payloadHash, Hash256? latestValidHash)
    {
        List<ProtoNode> invalid = [];
        bool latestValidFound = false;
        Hash256? next = payloadHash;
        foreach (ProtoNode node in _protoArray.EnumerateAncestorNodes(chainRoot))
        {
            if (node.ExecutionStatus == ExecutionStatus.Irrelevant)
                break;
            if (next is not null && node.ExecutionBlockHash != next)
                continue;
            if (latestValidHash != Hash256.Zero && node.ExecutionBlockHash == latestValidHash)
            {
                latestValidFound = _protoArray.IsFinalizedCheckpointOrDescendant(node.Root, _store.FinalizedCheckpoint);
                break;
            }

            invalid.Add(node);
            // specs/gloas/fork-choice.md get_parent_payload_status: follow bid parent hashes across EMPTY nodes.
            next = null;
            if (node.IsGloas && node.Parent is int parent && _protoArray.Nodes[parent].IsGloas && !_parentBlockHashes.TryGetValue(node.Root, out next))
                break;
        }

        latestValidFound |= latestValidHash == Hash256.Zero;
        // specs/bellatrix/optimistic-sync.md: an unknown latestValidHash acts as null, naming only the payload in question.
        int count = latestValidFound ? invalid.Count : payloadHash is null ? 0 : Math.Min(1, invalid.Count);
        invalid.RemoveRange(count, invalid.Count - count);
        IReadOnlyList<ProtoNode> nodes = _protoArray.Nodes;
        HashSet<int> subtreeRoots = [];
        foreach (ProtoNode node in invalid)
        {
            if (node.IsGloas ? _validPayloads.Contains(node.Root) : node.ExecutionStatus == ExecutionStatus.Valid)
                throw new ProtoArrayException($"Valid execution payload {node.ExecutionBlockHash} of block {node.Root} became invalid");
            if (!node.IsGloas)
            {
                subtreeRoots.Add(_protoArray.IndexOf(node.Root)!.Value);
                continue;
            }

            foreach (int child in node.Children)
            {
                if (nodes[child].ParentPayloadStatus == ForkChoicePayloadStatus.Full)
                    subtreeRoots.Add(child);
            }
        }

        InvalidateSubtrees(subtreeRoots);
        foreach (ProtoNode node in invalid)
        {
            if (!node.IsGloas)
                continue;
            _payloads.Remove(node.Root);
            _invalidPayloads.Add(node.Root);
        }
    }

    /// <summary>
    /// Applies an engine VALID verdict on the payload <paramref name="payloadHash"/> of the chain ending at <paramref name="chainRoot"/>:
    /// the nearest block that carries or builds on that payload, and all its ancestors, become valid.
    /// </summary>
    /// <remarks>
    /// specs/bellatrix/optimistic-sync.md: when a block becomes VALID, all its ancestors become VALID. Every beacon ancestor of a
    /// Gloas block builds on an execution ancestor of the payload that block builds on, so the walk holds across EMPTY and FULL nodes.
    /// </remarks>
    /// <exception cref="ProtoArrayException">An ancestor was invalidated before.</exception>
    public void ValidateExecutionChain(Hash256 chainRoot, Hash256 payloadHash)
    {
        foreach (ProtoNode node in _protoArray.EnumerateAncestorNodes(chainRoot))
        {
            bool carriesPayload = node.ExecutionBlockHash == payloadHash;
            if (!carriesPayload && (!node.IsGloas || GetParentBlockHash(node.Root) != payloadHash))
                continue;

            if (carriesPayload && _invalidPayloads.Contains(node.Root))
                throw new ProtoArrayException($"Invalid execution payload {node.ExecutionBlockHash} became valid");
            _protoArray.ProcessExecutionPayloadValidation(node.Root);
            if (carriesPayload && node.IsGloas)
                _validPayloads.Add(node.Root);
            return;
        }
    }

    /// <summary>specs/bellatrix/optimistic-sync.md: invalidates the blocks at <paramref name="subtreeRoots"/> and all their descendants, or none.</summary>
    /// <exception cref="ProtoArrayException">One of them is valid or has no execution payload.</exception>
    private void InvalidateSubtrees(HashSet<int> subtreeRoots)
    {
        if (subtreeRoots.Count == 0)
            return;

        IReadOnlyList<ProtoNode> nodes = _protoArray.Nodes;
        int start = int.MaxValue;
        foreach (int index in subtreeRoots)
            start = Math.Min(start, index);

        // specs/bellatrix/optimistic-sync.md: invalidate all descendants; parent-before-child storage permits one pass.
        bool[] invalid = new bool[nodes.Count];
        for (int i = start; i < nodes.Count; i++)
        {
            ProtoNode node = nodes[i];
            if (!subtreeRoots.Contains(i) && !(node.Parent is int parent && invalid[parent]))
                continue;
            if (node.ExecutionStatus is ExecutionStatus.Valid or ExecutionStatus.Irrelevant)
                throw new ProtoArrayException($"Block {node.Root} ({node.ExecutionStatus}) builds on an invalid execution payload");
            invalid[i] = true;
        }

        for (int i = start; i < nodes.Count; i++)
        {
            if (!invalid[i])
                continue;
            ProtoNode node = nodes[i];
            node.ExecutionStatus = ExecutionStatus.Invalid;
            node.BestChild = null;
            node.BestDescendant = null;
        }
    }

    /// <summary>
    /// The spec's <c>get_proposer_head</c> (specs/fulu/fork-choice.md): the block the proposer of
    /// <paramref name="proposalSlot"/> should build on. That is <paramref name="headRoot"/>'s parent when the
    /// late, weakly attested head can safely be re-orged out, or when the head is weak and its proposer
    /// equivocated in the slot before the proposal; <paramref name="headRoot"/> itself otherwise.
    /// </summary>
    /// <remarks>
    /// Weights are refreshed via <see cref="GetHead"/> first, because the spec's <c>get_attestation_score</c>
    /// is a live computation over current votes, not a value cached from an earlier <see cref="GetHead"/> call.
    /// There is no <c>is_shuffling_stable</c>: Fulu's proposer lookahead (EIP-7917) fixes the proposer before the
    /// epoch boundary. The conditions short-circuit, which the spec allows.
    /// </remarks>
    /// <exception cref="ForkChoiceException">
    /// <paramref name="headRoot"/> or its parent is unknown to fork choice, the head still holds the proposer
    /// boost (its score has not worn off), a state <c>is_head_weak</c> needs cannot be resolved, or the justified
    /// block has an invalid execution payload.
    /// </exception>
    public Hash256 GetProposerHead(Hash256 headRoot, ulong proposalSlot)
    {
        Hash256 parentRoot = _protoArray.GetParentRoot(headRoot)
            ?? throw new ForkChoiceException($"Block {headRoot} is unknown to fork choice, or has no parent to reorg onto");
        ulong headSlot = _protoArray.GetBlockSlot(headRoot) ?? throw new ForkChoiceException($"Block {headRoot} is unknown to fork choice");
        ulong parentSlot = _protoArray.GetBlockSlot(parentRoot) ?? throw new ForkChoiceException($"Block {parentRoot} is unknown to fork choice");

        if (_store.ProposerBoostRoot == headRoot)
            throw new ForkChoiceException($"Cannot evaluate a proposer reorg for {headRoot}: it still holds the proposer boost");

        GetHead();
        JustifiedBalances justifiedBalances = GetJustifiedBalances(_store.JustifiedCheckpoint);
        bool headWeak = IsHeadWeak(headRoot, headSlot, justifiedBalances);

        bool reorgLateHead = IsHeadLate(headRoot)
            && _protoArray.GetUnrealizedJustifiedCheckpoint(headRoot) == _protoArray.GetUnrealizedJustifiedCheckpoint(parentRoot)
            && IsFinalizationOk(proposalSlot, _store.FinalizedCheckpoint.Epoch, ReorgMaxEpochsSinceFinalization)
            && IsProposingOnTime()
            && IsSingleSlotReorg(parentSlot, headSlot, proposalSlot)
            && headWeak
            && IsParentStrong(parentRoot, justifiedBalances);
        bool reorgEquivocatingHead = headWeak && headSlot + 1 == proposalSlot && IsProposerEquivocation(headRoot);

        return reorgLateHead || reorgEquivocatingHead ? parentRoot : headRoot;
    }

    /// <summary>The spec's <c>is_head_late</c>: a block with no recorded timeliness (unknown to this store) is treated as late, denying a reorg rather than allowing one on missing data.</summary>
    private bool IsHeadLate(Hash256 headRoot) => !_blockTimeliness.TryGetValue(headRoot, out BlockTimeliness timeliness) || !timeliness.Attestation;

    /// <summary>The spec's <c>is_proposing_on_time</c>: whether the wall clock is at most <c>get_proposer_reorg_cutoff_ms</c> into the slot.</summary>
    private bool IsProposingOnTime()
    {
        ulong slotDurationMs = _spec.SecondsPerSlot * 1000;
        ulong secondsSinceGenesis = Time - GenesisTime;
        // The spec's seconds_to_milliseconds saturates at UINT64_MAX.
        ulong timeIntoSlotMs = (secondsSinceGenesis > ulong.MaxValue / 1000 ? ulong.MaxValue : secondsSinceGenesis * 1000) % slotDurationMs;
        return timeIntoSlotMs <= GloasTiming.ProposerReorgCutoffBps * slotDurationMs / Presets.BasisPoints;
    }

    /// <summary>
    /// The spec's <c>is_head_weak</c>: whether the head's attestation score, plus the justified effective balance
    /// of every equivocating validator in the head slot's committees, is below
    /// <see cref="ReorgHeadWeightThresholdPercent"/> of a committee's share.
    /// </summary>
    /// <remarks>
    /// The equivocators' balances are read unfiltered from the justified state: they are usually slashed, and
    /// <paramref name="justifiedBalances"/> reports slashed validators as zero. Valid only right after <see cref="GetHead"/>.
    /// </remarks>
    private bool IsHeadWeak(Hash256 headRoot, ulong headSlot, JustifiedBalances justifiedBalances)
    {
        ulong headWeight = GetAttestationScore(headRoot, justifiedBalances);
        // With no equivocators the committee term is zero, so the head state is not needed.
        if (_equivocatingIndices.Count != 0)
        {
            Validator[] justifiedValidators = ValidatorsOf(GetCheckpointState(_store.JustifiedCheckpoint));
            ulong headEpoch = BeaconStateAccessors.ComputeEpochAtSlot(headSlot);
            CommitteeCache committees = GetBlockState(headRoot) switch
            {
                ForkedBeaconState.OfFulu fulu => _committees.GetCommitteeCache(fulu.State, headEpoch),
                ForkedBeaconState.OfGloas gloas => _committees.GetCommitteeCache(gloas.State, headEpoch),
                ForkedBeaconState state => throw new NotSupportedException($"Unhandled block state {state.GetType().Name}"),
            };

            for (int index = 0; index < committees.CommitteesPerSlot; index++)
            {
                foreach (int member in committees.GetBeaconCommittee(headSlot, index))
                {
                    if (!_equivocatingIndices.Contains((ulong)member)) continue;
                    if (member >= justifiedValidators.Length)
                        throw new ForkChoiceException($"Equivocating validator {member} in the committees of slot {headSlot} is not in the justified state");
                    headWeight = checked(headWeight + justifiedValidators[member].EffectiveBalance);
                }
            }
        }

        return headWeight < _protoArray.CalculateCommitteeFraction(justifiedBalances, ReorgHeadWeightThresholdPercent);
    }

    /// <summary>The spec's <c>is_parent_strong</c>: whether the parent's attestation score is above <see cref="ReorgParentWeightThresholdPercent"/> of a committee's share. Valid only right after <see cref="GetHead"/>.</summary>
    private bool IsParentStrong(Hash256 parentRoot, JustifiedBalances justifiedBalances) =>
        GetAttestationScore(parentRoot, justifiedBalances) > _protoArray.CalculateCommitteeFraction(justifiedBalances, ReorgParentWeightThresholdPercent);

    /// <summary>
    /// The spec's <c>get_attestation_score</c>: the proto-array weight of <paramref name="root"/> without the proposer
    /// score that the last <see cref="GetHead"/> added to the boosted block and each of its ancestors.
    /// </summary>
    /// <remarks>
    /// Mirrors the proto-array's boost rule: the score is the default boost percent of a committee's share of
    /// <paramref name="justifiedBalances"/>, and an execution-invalid boost root is never boosted. Valid only right
    /// after <see cref="GetHead"/> with the same balances.
    /// </remarks>
    private ulong GetAttestationScore(Hash256 root, JustifiedBalances justifiedBalances)
    {
        ulong weight = _protoArray.GetWeight(root) ?? throw new ForkChoiceException($"Block {root} is unknown to fork choice");
        Hash256 boostRoot = _store.ProposerBoostRoot;
        if (boostRoot == Hash256.Zero || _protoArray.GetBlockExecutionStatus(boostRoot) == ExecutionStatus.Invalid)
            return weight;

        ulong slot = _protoArray.GetBlockSlot(root)!.Value;
        return _protoArray.GetAncestor(boostRoot, slot) == root
            ? checked(weight - _protoArray.CalculateCommitteeFraction(justifiedBalances, ProtoArrayForkChoice.DefaultProposerScoreBoostPercent))
            : weight;
    }

    /// <summary>The spec's <c>is_proposer_equivocation</c>: whether another block in the store has the same slot and proposer as <paramref name="root"/>.</summary>
    internal bool IsProposerEquivocation(Hash256 root)
    {
        if (!_blockProposers.TryGetValue(root, out BlockProposer proposer))
            return false;

        int matching = 0;
        foreach (BlockProposer other in _blockProposers.Values)
        {
            if (other == proposer && ++matching > 1)
                return true;
        }

        return false;
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

        ForkedBeaconState state = ComputeCheckpointState(checkpoint);
        _checkpointStates[checkpoint] = state;
        return state;
    }

    /// <summary>The target state <c>on_attestation</c> checks a vote against, without building one for a target whose shuffling a held state already has.</summary>
    /// <remarks>
    /// specs/phase0/fork-choice.md on_attestation reads only the committees, the signing domain and the registry size from
    /// <c>store_target_checkpoint_state</c>. The committees and the fork of a target epoch are fixed by its shuffling decision
    /// block (<see cref="BeaconStateAccessors.GetShufflingDecisionRoot(BeaconStateFulu, ulong)"/>), and every index the vote names
    /// comes from those committees, so a state of another target with the same decision block gives the same verdict. A vote for
    /// any known root then costs a state only when its decision block is new, and the caller caches that state only once the vote verifies.
    /// </remarks>
    /// <param name="unheld">Whether the state was built for this call and is held nowhere yet.</param>
    private ForkedBeaconState GetVoteTargetState(CheckpointRef target, out bool unheld)
    {
        unheld = false;
        ShufflingKey? key = GetShufflingKey(target);
        if (_checkpointStates.TryGetValue(target, out ForkedBeaconState? cached))
        {
            RegisterVoteState(key, cached);
            return cached;
        }

        if (key is { } shuffling && _voteStates.TryGetValue(shuffling, out ForkedBeaconState? shared))
            return shared;

        ForkedBeaconState state = ComputeCheckpointState(target);
        RegisterVoteState(key, state);
        unheld = true;
        return state;
    }

    /// <summary>The <see cref="ShufflingKey"/> of <paramref name="target"/>'s epoch on its chain; <c>null</c> for the first two epochs or a root the tree does not hold.</summary>
    private ShufflingKey? GetShufflingKey(CheckpointRef target)
    {
        if (target.Epoch <= Presets.MinSeedLookahead)
            return null;

        ulong decisionSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(target.Epoch - Presets.MinSeedLookahead) - 1;
        Hash256? treeRoot = null;
        foreach (ProtoNode node in _protoArray.EnumerateAncestorNodes(target.Root))
        {
            if (node.Slot <= decisionSlot)
                return new ShufflingKey(target.Epoch, node.Root, BelowTreeRoot: false);
            treeRoot = node.Root;
        }

        return treeRoot is null ? null : new ShufflingKey(target.Epoch, treeRoot, BelowTreeRoot: true);
    }

    /// <summary>Holds <paramref name="state"/> for its shuffling unless one is held, dropping the shufflings of epochs no gossip vote can target any more.</summary>
    private void RegisterVoteState(ShufflingKey? key, ForkedBeaconState state)
    {
        if (key is not { } shuffling || _voteStates.ContainsKey(shuffling))
            return;

        Hash256 decisionRoot = state switch
        {
            ForkedBeaconState.OfFulu fulu => fulu.State.GetShufflingDecisionRoot(shuffling.Epoch),
            ForkedBeaconState.OfGloas gloas => gloas.State.GetShufflingDecisionRoot(shuffling.Epoch),
            _ => throw new NotSupportedException($"Unhandled checkpoint state {state.GetType().Name}"),
        };
        // The walk and the state's own block roots must name the same block, or the state stands in for no other target.
        if (!shuffling.BelowTreeRoot && decisionRoot != shuffling.Root)
            return;

        ulong currentEpoch = _store.CurrentEpoch;
        List<ShufflingKey>? stale = null;
        foreach (ShufflingKey held in _voteStates.Keys)
        {
            if (held.Epoch + 1 < currentEpoch) (stale ??= []).Add(held);
        }

        if (stale is not null)
        {
            foreach (ShufflingKey held in stale) _voteStates.Remove(held);
        }

        _voteStates[shuffling] = state;
    }

    /// <summary>The spec's <c>store_target_checkpoint_state</c> computation, without the cache; see <see cref="GetCheckpointState"/>.</summary>
    private ForkedBeaconState ComputeCheckpointState(CheckpointRef checkpoint)
    {
        ForkedBeaconState state = GetBlockState(checkpoint.Root);
        ulong startSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(checkpoint.Epoch);
        if (state.Slot < startSlot)
        {
            state = AdvanceCopy(checkpoint.Root, state, startSlot, IsGloasSlot(startSlot) ? BeaconFork.Gloas : BeaconFork.Fulu);
            // The epoch transitions above can apply pending deposits and grow the registry.
            ExtendPubkeys(ValidatorsOf(state));
        }

        return state;
    }

    /// <summary>A mutable copy of <paramref name="blockState"/> advanced to <paramref name="targetSlot"/>, crossing into <paramref name="targetFork"/> on the way when needed.</summary>
    private ForkedBeaconState AdvanceCopy(Hash256 blockRoot, ForkedBeaconState blockState, ulong targetSlot, BeaconFork targetFork)
    {
        EpochCache cache = new() { Hasher = new BlockStateRootFirst(_protoArray.EnumerateAncestorNodes(blockRoot).First().StateRoot, CheckpointStateHasher()) };
        ForkedBeaconState state = blockState switch
        {
            ForkedBeaconState.OfFulu => new ForkedBeaconState.OfFulu(_stateProvider.CopyBlockState(blockRoot)
                ?? throw new ForkChoiceException($"No state for the block {blockRoot}")),
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

    /// <summary>Answers the first <c>process_slot</c> root of a checkpoint advance with the block's own <c>state_root</c>, and hashes every later slot with <paramref name="next"/>.</summary>
    /// <remarks>
    /// The advance starts from <c>store.block_states[root]</c>, the post-state whose root the block's <c>state_root</c> commits to (specs/phase0/beacon-chain.md
    /// <c>state_transition</c>; the anchor's is checked at construction), so a one-slot advance needs no merkleization.
    /// </remarks>
    private sealed class BlockStateRootFirst(Hash256 blockStateRoot, IBeaconStateHasher next) : IBeaconStateHasher
    {
        private bool _started;

        public Hash256 HashTreeRoot(BeaconStateFulu state) => _started ? next.HashTreeRoot(state) : Start();

        public Hash256 HashTreeRoot(BeaconStateGloas state) => _started ? next.HashTreeRoot(state) : Start();

        private Hash256 Start()
        {
            _started = true;
            return blockStateRoot;
        }
    }

    private static Validator[] ValidatorsOf(ForkedBeaconState state) => state switch
    {
        ForkedBeaconState.OfFulu fulu => fulu.State.Validators!,
        ForkedBeaconState.OfGloas gloas => gloas.State.Validators!,
        _ => throw new NotSupportedException($"Unhandled state {state.GetType().Name}"),
    };

    /// <summary>The spec's <c>get_weight</c> balance source: effective balances of the justified state's active, unslashed validators, and its <c>get_total_active_balance</c>.</summary>
    private JustifiedBalances GetJustifiedBalances(CheckpointRef justified)
    {
        if (_justifiedBalances.TryGetValue(justified, out JustifiedBalances? cached))
            return cached;

        ForkedBeaconState state = GetCheckpointState(justified);
        ulong epoch = BeaconStateAccessors.ComputeEpochAtSlot(state.Slot);
        Validator[] validators = ValidatorsOf(state);
        ulong[] effectiveBalances = new ulong[validators.Length];
        ulong totalActiveBalance = 0;
        for (int i = 0; i < validators.Length; i++)
        {
            Validator validator = validators[i];
            if (!validator.IsActiveValidator(epoch))
                continue;

            totalActiveBalance = checked(totalActiveBalance + validator.EffectiveBalance);
            if (!validator.Slashed)
                effectiveBalances[i] = validator.EffectiveBalance;
        }

        // get_total_active_balance counts slashed validators and floors at EFFECTIVE_BALANCE_INCREMENT (specs/phase0/beacon-chain.md).
        JustifiedBalances balances = new(effectiveBalances, Math.Max(Presets.EffectiveBalanceIncrement, totalActiveBalance));
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
                ApplyVotes(queued.AttestingIndices, queued.BlockRoot, queued.Slot, queued.TargetEpoch, queued.PayloadPresent);
                _queuedAttestations.RemoveAt(i);
            }
        }
    }

    /// <summary>The spec's <c>update_latest_messages</c>: equivocating validators never vote again.</summary>
    private void ApplyVotes(ulong[] attestingIndices, Hash256 blockRoot, ulong slot, ulong targetEpoch, bool? payloadPresent)
    {
        foreach (ulong index in attestingIndices)
        {
            if (!_equivocatingIndices.Contains(index))
                _protoArray.ProcessAttestation(index, blockRoot, slot, targetEpoch, payloadPresent);
        }
    }

    private void ExtendPubkeys(Validator[] validators)
    {
        if (validators.Length > _pubkeys.Count)
            _pubkeys.Extend(validators, _pubkeys.Count);
    }
}
