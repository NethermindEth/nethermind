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
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;

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
/// fork choice and for verifying the block's execution payload envelope. A child of the last Gloas block
/// reuses its <see cref="CachedBeaconStateHasher"/>, which the envelope's state-root check shares; a fork
/// branch starts with a fresh one.
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
    private static readonly StringLabel GossipPayloadAttestationRejected = new("gossip_payload_attestation");

    private const int MaxDeferredBlocks = 256;

    private const int MaxUntrustedRegenerationsPerSlot = 2;

    private const int MaxByRootRegenerationsPerSlot = 2;

    private readonly BeaconChainSpec _spec;
    private readonly BeaconChainStore _store;
    private readonly PubkeyCache _pubkeys;
    private readonly IEngineDriver _engine;
    private readonly IBeaconChainConfig _config;
    private readonly SlotClock _clock;
    private readonly ILogger _logger;

    private readonly IDataAvailabilityRule _availability;

    private readonly PostStateCache _states;
    private readonly ForkChoiceRunner _runner;
    private readonly ExecutionPayloadEnvelopeImporter _envelopes;
    private readonly ForkChoiceSnapshotHolder? _forkChoiceSnapshots;
    private readonly ProposerLookaheadHolder? _proposerLookaheads;
    private readonly FailedBlockRoots? _failedBlocks;

    private readonly Dictionary<Hash256, ulong> _unfinalized = [];

    /// <summary>
    /// Blocks last answered <see cref="BlockImportResult.ParentPayloadUnverified"/>, keyed by root, with the nearest ancestor fork
    /// choice holds, whose state checked their proposer; bounded by <see cref="MaxDeferredBlocks"/> and pruned at finalization.
    /// </summary>
    private readonly Dictionary<Hash256, DeferredBlock> _deferred = [];

    private EpochCache _lineageCache = new() { Hasher = new CachedBeaconStateHasher() };

    private bool _lineageBlockStartsEpoch = true;

    /// <summary>Null until the first index update, so a restart reconciles the index even when the head is still the anchor.</summary>
    private Hash256? _canonicalHead;
    private ulong _canonicalIndexTopSlot;
    private ulong _lastSnapshotEpoch;

    /// <summary>The last Gloas block imported; only its children may reuse <see cref="_gloasLineageCache"/>, which refuses a memo built on another branch.</summary>
    private Hash256? _gloasLineageRoot;

    private EpochCache _gloasLineageCache = new() { Hasher = new CachedBeaconStateHasher() };

    /// <summary>A Gloas anchor's root and its bid's <c>parent_block_hash</c>, which fork choice records only for blocks it imported; <c>null</c> for a Fulu anchor.</summary>
    private readonly Hash256? _gloasAnchorRoot;

    private readonly Hash256? _gloasAnchorParentBlockHash;

    private readonly Hash256? _fuluProposerDomain;

    private readonly Hash256 _gloasProposerDomain = null!;

    private ulong _regenerationSlot;

    private int _regenerationsThisSlot;

    private readonly Dictionary<(ulong Slot, ulong ProposerIndex), Hash256> _regenerationProposals = [];

    private int _byRootRegenerationsThisSlot;

    private RequestedImport _importing;

    private enum RequestedImport
    {
        None,
        Range,
        ByRoot,
    }

    private readonly record struct DeferredBlock(Hash256 AncestorRoot, ulong Slot);

    /// <param name="isEnvelopeDataAvailable">The Gloas <c>is_data_available</c> an execution payload envelope is checked against; see <see cref="ExecutionPayloadEnvelopeImporter"/>.</param>
    /// <param name="clock">The node's slot clock; a block after its current slot (within <c>MAXIMUM_GOSSIP_CLOCK_DISPARITY</c>) is refused.</param>
    /// <param name="anchorState">The post-state of <paramref name="anchorBlock"/>, of the same fork.</param>
    /// <param name="forkChoiceSnapshots">Where <see cref="ComputeHead"/> publishes a copy of the fork-choice store for readers off the import thread; <c>null</c> publishes nothing.</param>
    /// <param name="proposerLookaheads">Where <see cref="ComputeHead"/> publishes the head state's proposer lookahead; <c>null</c> publishes nothing.</param>
    /// <param name="failedBlocks">Where the root of a block refused for failing validation is recorded, until it finalizes; <c>null</c> records nothing.</param>
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
        ForkChoiceSnapshotHolder? forkChoiceSnapshots = null,
        ProposerLookaheadHolder? proposerLookaheads = null,
        FailedBlockRoots? failedBlocks = null)
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
        _proposerLookaheads = proposerLookaheads;
        _failedBlocks = failedBlocks;

        switch (anchorState, anchorBlock)
        {
            case (ForkedBeaconState.OfFulu { State: BeaconStateFulu fuluState }, ForkedSignedBeaconBlock.OfFulu { Block: SignedBeaconBlock fuluBlock }):
                _states = new PostStateCache(store, spec, anchorRoot, fuluState, IsGloasBlock, GetJustifiedRoot, logManager, IsAboveFinalized, pubkeys, AncestorRoots);
                _runner = new ForkChoiceRunner(spec, fuluState, fuluBlock.Message!, _states, pubkeys, _states, logManager);
                _lastSnapshotEpoch = fuluState.GetCurrentEpoch();
                _fuluProposerDomain = Domains.ComputeDomain(DomainType.BeaconProposer, fuluState.Fork!.CurrentVersion!, fuluState.GenesisValidatorsRoot!);
                _gloasProposerDomain = Domains.ComputeDomain(DomainType.BeaconProposer, spec.GloasForkVersion, fuluState.GenesisValidatorsRoot!);
                break;
            case (ForkedBeaconState.OfGloas { State: BeaconStateGloas gloasState }, ForkedSignedBeaconBlock.OfGloas { Block: SignedBeaconBlockGloas gloasBlock }):
                // specs/gloas/fork-choice.md get_forkchoice_store: block_states holds the anchor state, the finalized checkpoint's.
                _states = new PostStateCache(store, spec, null, null, IsGloasBlock, GetJustifiedRoot, logManager, IsAboveFinalized, pubkeys, AncestorRoots);
                _states.PinGloas(anchorRoot, gloasState);
                _runner = new ForkChoiceRunner(spec, gloasState, gloasBlock.Message!, _states, pubkeys, _states, logManager);
                _gloasAnchorRoot = anchorRoot;
                _gloasAnchorParentBlockHash = gloasBlock.Message!.Body!.SignedExecutionPayloadBid!.Message!.ParentBlockHash;
                _lastSnapshotEpoch = gloasState.GetCurrentEpoch();
                _gloasProposerDomain = Domains.ComputeDomain(DomainType.BeaconProposer, gloasState.Fork!.CurrentVersion!, gloasState.GenesisValidatorsRoot!);
                break;
            default:
                throw new ArgumentException($"The anchor state at slot {anchorState.Slot} is a {anchorState.Fork} state, but its block at slot {anchorBlock.Slot} is a {anchorBlock.GetType().Name} block", nameof(anchorBlock));
        }

        _envelopes = new ExecutionPayloadEnvelopeImporter(_states, engine, pubkeys, isEnvelopeDataAvailable, logManager, () => _gloasLineageCache.Hasher);
        store.SetCanonicalRoot(anchorBlock.Slot, anchorRoot);
        // A restart must clear what the previous run indexed above the head it replays up to.
        _canonicalIndexTopSlot = Math.Max(anchorBlock.Slot, store.GetCanonicalIndexTopSlot() ?? 0);
    }

    /// <inheritdoc/>
    public bool IsKnown(Hash256 blockRoot) => _runner.ContainsBlock(blockRoot);

    bool IBlockImporter.IsHeadStale => _runner.IsHeadStale;

    /// <summary>The block whose post-state a Fulu child imports onto without a state copy; for tests.</summary>
    internal Hash256? LineageRoot => _states.LineageRoot;

    private bool IsGloasBlock(Hash256 blockRoot) => _runner.GetBlockSlot(blockRoot) is ulong slot && SignedBeaconBlockCodec.IsGloasSlot(slot, _spec);

    private Hash256 GetJustifiedRoot() => _runner.JustifiedCheckpoint.Root;

    private IEnumerable<Hash256> AncestorRoots(Hash256 blockRoot)
    {
        foreach (ProtoNode node in _runner.EnumerateAncestors(blockRoot))
        {
            yield return node.Root;
        }
    }

    private bool IsAboveFinalized(Hash256 blockRoot) => IsAboveFinalized(_runner, blockRoot);

    /// <summary>Whether <paramref name="runner"/> holds <paramref name="blockRoot"/> above the finalized epoch's start slot, so the block can still become justified.</summary>
    /// <remarks>A block at or below that slot is the finalized checkpoint block or off its chain, and a block fork choice pruned is off it too.</remarks>
    internal static bool IsAboveFinalized(ForkChoiceRunner runner, Hash256 blockRoot) =>
        runner.GetBlockSlot(blockRoot) is ulong slot && slot > BeaconStateAccessors.ComputeStartSlotAtEpoch(runner.FinalizedCheckpoint.Epoch);

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

    /// <summary>Forgets the deferral entry of a block the orchestrator dropped, so it does not hold a share of <see cref="MaxDeferredBlocks"/> until finality.</summary>
    internal void Release(Hash256 blockRoot) => _deferred.Remove(blockRoot);

    /// <inheritdoc/>
    public BlockImportResult ImportRequested(ForkedSignedBeaconBlock block, Hash256 blockRoot, bool fetchedByRoot = false)
    {
        _importing = fetchedByRoot ? RequestedImport.ByRoot : RequestedImport.Range;
        try
        {
            return Import(block, blockRoot, verifySignatures: true);
        }
        finally
        {
            _importing = RequestedImport.None;
        }
    }

    /// <inheritdoc/>
    public BlockImportResult Import(ForkedSignedBeaconBlock block, Hash256 blockRoot, bool verifySignatures)
    {
        LastRefusal = ImportRefusal.None;
        RejectGossip = false;
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
            BlockImportResult result = verifySignatures && block is ForkedSignedBeaconBlock.OfGloas gloasChild && _deferred.TryGetValue(block.ParentRoot, out DeferredBlock deferredParent)
                ? DeferBehindDeferredParent(gloasChild.Block, blockRoot, deferredParent)
                : BlockImportResult.UnknownParent;
            // ethereum/consensus-specs gloas/p2p-interface.md: "[IGNORE] The block's parent has been seen (via gossip or non-gossip sources)".
            RejectGossip = false;
            return result;
        }

        if (CheckBeforeTransition(block.Slot, block.ParentRoot, out bool failedValidation, out bool rejectGossip) is { } refusal)
        {
            RejectGossip = rejectGossip && verifySignatures && CanRejectBlock(block);
            // specs/phase0/beacon-chain.md process_block_header: a block not after its parent's slot is invalid data, whatever this node's store.
            LastRefusal = block.Slot > _runner.GetBlockSlot(block.ParentRoot) ? ImportRefusal.LocalAdmission : ImportRefusal.None;
            if (_logger.IsWarn) _logger.Warn($"Dropping block {blockRoot} at slot {block.Slot} before its state transition: {refusal}");
            if (failedValidation)
            {
                _failedBlocks?.Add(blockRoot, block.Slot);
            }

            return BlockImportResult.Invalid;
        }

        // fork-choice.md on_block: a trusted replay must wait before mutating its lineage state in place.
        if (!verifySignatures && IsBeforeItsSlot(block.Slot, blockRoot))
        {
            return BlockImportResult.FutureSlot;
        }

        if (block is ForkedSignedBeaconBlock.OfGloas && HasInvalidGossipFields(block))
        {
            RejectGossip = verifySignatures && CanRejectBlock(block);
            _failedBlocks?.Add(blockRoot, block.Slot);
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
    /// <param name="failedValidation"><c>true</c> when the refusal is a validation failure that no later time can undo; <c>false</c> for a block from a future slot or at or below the finalized slot.</param>
    /// <param name="rejectGossip">Whether the failed condition can reject gossip after all preceding checks pass.</param>
    /// <returns>Why the block is refused, or <c>null</c>.</returns>
    /// <remarks>
    /// The current slot is the node's clock, allowing <c>MAXIMUM_GOSSIP_CLOCK_DISPARITY</c> as gossip does, never
    /// fork-choice time: a block within that allowance of its slot's start waits for the slot (<see cref="IsBeforeItsSlot"/>).
    /// </remarks>
    private string? CheckBeforeTransition(ulong slot, Hash256 parentRoot, out bool failedValidation, out bool rejectGossip)
    {
        failedValidation = false;
        rejectGossip = false;
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
                failedValidation = true;
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
            if (checkpointBlock is not null || !_runner.IsBootstrapCheckpointDescendant(parentRoot, finalized))
            {
                // ethereum/consensus-specs gloas/p2p-interface.md: "[REJECT] The current finalized checkpoint is an ancestor of the block".
                rejectGossip = true;
                failedValidation = true;
                return $"the block does not descend from the finalized checkpoint {finalized}";
            }
        }

        ulong parentSlot = _runner.GetBlockSlot(parentRoot)!.Value;
        failedValidation = slot <= parentSlot;
        // ethereum/consensus-specs gloas/p2p-interface.md: "[REJECT] The block is from a higher slot than its parent".
        if (failedValidation) rejectGossip = true;
        return slot > parentSlot ? null : $"the block is not after its parent's slot {parentSlot}";
    }

    private bool CanRejectBlock(ForkedSignedBeaconBlock block)
    {
        bool? signatureValid = CheckGossipSignature(block);
        // ethereum/consensus-specs gloas/p2p-interface.md: "[REJECT] The proposer signature is valid".
        if (signatureValid != true)
            return signatureValid == false;

        // ethereum/consensus-specs gloas/p2p-interface.md: "[IGNORE] If the parent block is full, the parent payload is valid".
        return block is not ForkedSignedBeaconBlock.OfGloas gloas
            || !_runner.IsParentNodeFull(gloas.Block.Message!) || _runner.IsPayloadVerified(block.ParentRoot);
    }

    private bool? CheckGossipSignature(ForkedSignedBeaconBlock block)
    {
        Hash256 head = _runner.GetHead();
        int? validatorCount = _states.GetGloasBlockState(head)?.Validators?.Length ?? _states.GetHeldBlockState(head)?.Validators?.Length;
        Hash256? domain = block is ForkedSignedBeaconBlock.OfGloas ? _gloasProposerDomain : _fuluProposerDomain;
        if (validatorCount is null || domain is null)
            return null;
        ulong proposer = block.ProposerIndex;
        if (proposer >= (ulong)validatorCount.Value)
            return false;
        if (proposer >= (ulong)_pubkeys.Count)
            return null;
        BlsSignature signature = block is ForkedSignedBeaconBlock.OfGloas gloas ? gloas.Block.Signature : ((ForkedSignedBeaconBlock.OfFulu)block).Block.Signature;
        return _pubkeys.TryGetValidPublicKey((int)proposer, out G1Affine key)
            && BlsSigner.Verify(key, signature.Bytes, Domains.ComputeSigningRoot(block.ComputeMessageRoot(), domain).Bytes);
    }

    private bool HasInvalidGossipFields(ForkedSignedBeaconBlock block)
    {
        ulong blobCount;
        if (block is ForkedSignedBeaconBlock.OfGloas gloas)
        {
            ExecutionPayloadBid bid = gloas.Block.Message!.Body!.SignedExecutionPayloadBid!.Message!;
            // ethereum/consensus-specs gloas/p2p-interface.md: "[REJECT] The bid's parent equals the block's parent".
            if (bid.ParentBlockRoot != block.ParentRoot)
                return true;
            blobCount = (ulong)(bid.BlobKzgCommitments?.Length ?? 0);
        }
        else
        {
            BeaconBlockBody body = ((ForkedSignedBeaconBlock.OfFulu)block).Block.Message!.Body!;
            // ethereum/consensus-specs fulu/p2p-interface.md: "[REJECT] The block's execution payload timestamp is correct with respect to the slot".
            if (body.ExecutionPayload!.Timestamp != (UInt128)_runner.GenesisTime + (UInt128)block.Slot * Presets.SecondsPerSlot)
                return true;
            blobCount = (ulong)(body.BlobKzgCommitments?.Length ?? 0);
        }

        // ethereum/consensus-specs fulu/p2p-interface.md: "[REJECT] The length of KZG commitments is less than or equal to the limit".
        return blobCount > (_spec.GetBlobParameters(_spec.GetEpoch(block.Slot))?.MaxBlobsPerBlock ?? _spec.MaxBlobsPerBlockElectra);
    }

    private sealed class ConfirmedBlockAvailability(Hash256 confirmedRoot) : IDataAvailabilityRule
    {
        public bool IsDataAvailable(BeaconBlock block, Hash256 blockRoot, BeaconChainSpec spec) => blockRoot == confirmedRoot;
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
            if (_logger.IsDebug) _logger.Debug($"Deferring block {blockRoot} at slot {block.Slot}: blob data is not yet available");
            return BlockImportResult.DataUnavailable;
        }

        bool onLineage = parentRoot == _states.LineageRoot;
        // Kept frozen: the body operations below may load states that push it out of the retained tiers before it is marked.
        BeaconStateFulu? heldParent = null;
        if (!onLineage && (heldParent = _states.GetHeldBlockState(parentRoot)) is null)
        {
            if (verifySignatures && RefuseRegeneration(blockRoot, block.Slot, block.ProposerIndex, signedBlock.Signature, _fuluProposerDomain) is { } refused)
            {
                return refused;
            }

            heldParent = _states.GetBlockState(parentRoot);
        }

        BeaconStateFulu? parentState = onLineage ? _states.LineageState : heldParent?.Clone();
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
        // specs/phase0/fork-choice.md get_checkpoint_block: the parent is one when it sits at an epoch start slot or this block skips one.
        bool parentIsCheckpoint = parentState.Slot % _spec.SlotsPerEpoch == 0 || blockEpoch > parentEpoch + (block.Slot % _spec.SlotsPerEpoch == 0 ? 1UL : 0UL);

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
                _states.Retain(parentRoot, parentState.Clone(), parentIsCheckpoint);
            }
        }
        else
        {
            if (_logger.IsDebug) _logger.Debug($"Importing block {blockRoot} at slot {block.Slot} on a copy of its parent's state and without the cached hasher: the lineage is at {_states.LineageRoot}");
            state = parentState; // already a copy
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
            // An incomplete key cache cannot convict a validator whose index passed the registry bound.
            if (verifySignatures && !e.RejectGossip && block.ProposerIndex >= (ulong)_pubkeys.Count)
                return BlockImportResult.UnknownParent;
            if (_logger.IsWarn) _logger.Warn($"Dropping invalid block {blockRoot} at slot {block.Slot}: {e.Message}");
            // ethereum/consensus-specs fulu/p2p-interface.md: "[REJECT] The proposer signature is valid"; unrelated transition failures are not gossip rejections.
            RejectGossip = verifySignatures && (e is ProposerSignatureException || e.RejectGossip || HasInvalidGossipFields(new ForkedSignedBeaconBlock.OfFulu(signedBlock)))
                && CanRejectBlock(new ForkedSignedBeaconBlock.OfFulu(signedBlock));
            MarkFailed(blockRoot, block.Slot, e);
            // specs/bellatrix/optimistic-sync.md: the optimistic blocks after the payload latestValidHash names are invalid too.
            if (verdict.Status == ExecutionStatus.Invalid)
                _runner.InvalidateExecutionChain(parentRoot, payloadHash: null, verdict.LatestValidHash);
            return BlockImportResult.Invalid;
        }
        finally
        {
            _engine.CurrentBlock = null;
        }

        // The transition hook already drove engine_newPayload; an INVALID verdict made Apply throw.
        ExecutionStatus executionStatus = verdict.Status;
        if (IsBeforeItsSlot(block.Slot, blockRoot))
        {
            return BlockImportResult.FutureSlot;
        }

        TickToClock(block.Slot, receivedMs);

        // Spec Fulu on_block asserts is_data_available before state_transition; reuse that verdict only for this root.
        try
        {
            _runner.OnBlock(signedBlock, state, executionStatus, new ConfirmedBlockAvailability(blockRoot));
        }
        catch (ForkChoiceException e)
        {
            LastRefusal = ImportRefusal.LocalAdmission;
            if (_logger.IsWarn) _logger.Warn($"Dropping block {blockRoot} at slot {block.Slot} rejected by fork choice: {e.Message}");
            RecordIfRefusalIsPermanent(blockRoot, block.Slot, parentRoot);
            return BlockImportResult.Invalid;
        }

        if (executionStatus is ExecutionStatus.Valid)
        {
            _runner.OnValidExecutionPayload(blockRoot);
        }

        ApplyBodyOperations(block.Body!, blockRoot);

        _store.PutBlock(blockRoot, signedBlock);
        _unfinalized[blockRoot] = block.Slot;

        if (onLineage)
        {
            if (verifySignatures && (crossesEpoch || _lineageBlockStartsEpoch))
            {
                // The pre-clone original is the parent post-state, retained as-is.
                _states.Retain(parentRoot, parentState, parentIsCheckpoint);
            }

            _states.SetLineage(blockRoot, state);
            _lineageBlockStartsEpoch = crossesEpoch;
            MaybeSnapshotState(blockRoot, state, blockEpoch);
        }
        else
        {
            _states.Retain(blockRoot, state);
            if (parentIsCheckpoint)
            {
                _states.Retain(parentRoot, heldParent!, checkpointCandidate: true);
            }
        }

        return BlockImportResult.Imported;
    }

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
        BeaconStateFulu? fuluParent = null;
        ForkedBeaconState? parentState;
        bool gloasParentSlot = SignedBeaconBlockCodec.IsGloasSlot(parentSlot, _spec);
        if (gloasParentSlot)
        {
            gloasParent = _states.GetGloasBlockState(parentRoot);
        }
        else
        {
            fuluParent = _states.GetHeldBlockState(parentRoot);
        }

        if (gloasParent is null && fuluParent is null)
        {
            if (verifySignatures && RefuseRegeneration(blockRoot, block.Slot, block.ProposerIndex, signedBlock.Signature, _gloasProposerDomain) is { } refused)
            {
                return refused;
            }

            if (gloasParentSlot)
            {
                gloasParent = _states.GetOrRegenerateGloasBlockState(parentRoot);
            }
            else
            {
                fuluParent = _states.GetBlockState(parentRoot);
            }
        }

        parentState = gloasParent is not null ? new ForkedBeaconState.OfGloas(gloasParent.Clone())
            : fuluParent is not null ? new ForkedBeaconState.OfFulu(fuluParent.Clone())
            : null;

        if (parentState is null)
        {
            if (_logger.IsWarn) _logger.Warn($"Cannot import block at slot {block.Slot}: the post-state of its parent {parentRoot} is no longer retained");
            return BlockImportResult.UnknownParent;
        }

        // A fork block hashes with the stateless hasher, so one that fails allocates no memo and cannot disturb the lineage's.
        bool extendsLineage = parentRoot == _gloasLineageRoot;
        EpochCache cache = extendsLineage ? _gloasLineageCache : new EpochCache();
        BeaconStateGloas postState;
        try
        {
            // On a copy: the transition mutates before its last check, and UpgradeToGloas aliases the Fulu parent's arrays.
            postState = ((ForkedBeaconState.OfGloas)ForkedStateTransition.Apply(parentState, forked, cache, _pubkeys, new ImportVerdict(_engine), _spec, validateResult: true, verifySignatures)).State;
        }
        catch (BeaconStateException e)
        {
            // An incomplete key cache cannot convict a validator whose index passed the registry bound.
            if (verifySignatures && !e.RejectGossip && block.ProposerIndex >= (ulong)_pubkeys.Count)
                return BlockImportResult.UnknownParent;
            if (_logger.IsWarn) _logger.Warn($"Dropping invalid block {blockRoot} at slot {block.Slot}: {e.Message}");
            // ethereum/consensus-specs fulu/p2p-interface.md: "[REJECT] The proposer signature is valid"; unrelated transition failures are not gossip rejections.
            RejectGossip = verifySignatures && (e is ProposerSignatureException || e.RejectGossip) && CanRejectBlock(forked);
            MarkFailed(blockRoot, block.Slot, e);
            return BlockImportResult.Invalid;
        }

        if (parentPayloadUnverified)
        {
            // Recorded before OnBlock, which needs it: a stored full child was persisted only after passing that gate, so the record holds even if OnBlock now refuses it.
            _runner.OnExecutionPayloadVerified(parentRoot);
        }

        if (IsBeforeItsSlot(block.Slot, blockRoot))
        {
            return BlockImportResult.FutureSlot;
        }

        TickToClock(block.Slot, receivedMs);
        try
        {
            _runner.OnBlock(signedBlock, postState);
        }
        catch (Exception e) when (e is ForkChoiceException or BeaconStateException)
        {
            LastRefusal = ImportRefusal.LocalAdmission;
            if (_logger.IsWarn) _logger.Warn($"Dropping block {blockRoot} at slot {block.Slot} rejected by fork choice: {e.Message}");
            RecordIfRefusalIsPermanent(blockRoot, block.Slot, parentRoot);
            return BlockImportResult.Invalid;
        }

        // Only after OnBlock: the Gloas tier must hold states of blocks fork choice accepted (the spec's store.block_states).
        bool startsEpoch = block.Slot % _spec.SlotsPerEpoch == 0;
        _states.RetainGloas(blockRoot, postState, checkpointCandidate: startsEpoch);
        // The parent is the checkpoint block of every epoch whose start slot this block skipped.
        if (_spec.GetEpoch(block.Slot) > _spec.GetEpoch(parentSlot) + (startsEpoch ? 1UL : 0UL))
        {
            if (gloasParent is not null)
            {
                _states.RetainGloas(parentRoot, gloasParent, checkpointCandidate: true);
            }
            else if (fuluParent is not null)
            {
                // The live lineage state advances in place, so it is retained as a copy.
                _states.Retain(parentRoot, parentRoot == _states.LineageRoot ? fuluParent.Clone() : fuluParent, checkpointCandidate: true);
            }
        }

        ApplyBodyOperations(block.Body!, blockRoot);

        _store.PutForkedBlock(blockRoot, forked);
        _unfinalized[blockRoot] = block.Slot;
        if (!extendsLineage)
        {
            cache.Hasher = new CachedBeaconStateHasher();
        }

        _gloasLineageRoot = blockRoot;
        _gloasLineageCache = cache;
        return BlockImportResult.Imported;
    }

    /// <summary>
    /// Whether an untrusted block may cost the regeneration of a state that is not held: only once its proposer signature
    /// verifies. A block from gossip must also be the first for its slot and proposer to cost one, and fit in
    /// <see cref="MaxUntrustedRegenerationsPerSlot"/> per wall-clock slot. A range-sync block need not; a block fetched by root
    /// fits in <see cref="MaxByRootRegenerationsPerSlot"/> of its own, since any gossip block can make this node fetch one.
    /// </summary>
    /// <remarks>
    /// The signature is checked without the missing state: with the cached proposer key, and the domain of the block's fork
    /// (specs/phase0/beacon-chain.md <c>get_domain</c>), whose fork version every state of that fork after the anchor shares.
    /// Gossip validation does not verify it, so without this check a forged child of each evicted block buys a replay. Any
    /// cached key signs, so the budget bounds what signed gossip can cost, and blocks this node fetched, a competing branch
    /// from range sync or an ancestor a gossip block named, never wait on it. A gossip block refused here is recovered by
    /// the by-root backfill of its first child. phase0/p2p-interface.md <c>beacon_block</c> ignores all but the first block
    /// with a valid signature per slot and proposer, so a later one costs no regeneration.
    /// </remarks>
    /// <returns><c>null</c> when the regeneration may run; otherwise the result to answer for the block.</returns>
    private BlockImportResult? RefuseRegeneration(Hash256 blockRoot, ulong slot, ulong proposerIndex, BlsSignature signature, Hash256? domain)
    {
        if (domain is null || proposerIndex >= (ulong)_pubkeys.Count || !_pubkeys.TryGetValidPublicKey((int)proposerIndex, out G1Affine key))
        {
            if (_logger.IsDebug) _logger.Debug($"Not regenerating a state for block {blockRoot} at slot {slot}: its proposer {proposerIndex} has no cached key");
            return BlockImportResult.UnknownParent;
        }

        if (!BlsSigner.Verify(key, signature.Bytes, Domains.ComputeSigningRoot(blockRoot, domain).Bytes))
        {
            // ethereum/consensus-specs fulu/p2p-interface.md: "[REJECT] The proposer signature is valid".
            RejectGossip = true;
            if (_logger.IsWarn) _logger.Warn($"Dropping invalid block {blockRoot} at slot {slot}: invalid proposer signature");
            return BlockImportResult.Invalid;
        }

        if (_importing == RequestedImport.Range)
        {
            return null;
        }

        ulong currentSlot = _clock.CurrentSlot;
        if (currentSlot != _regenerationSlot)
        {
            _regenerationSlot = currentSlot;
            _regenerationsThisSlot = 0;
            _byRootRegenerationsThisSlot = 0;
            // on_block refuses a block at or below the finalized epoch's start slot, so no later block repeats its (slot, proposer).
            ulong finalizedSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(_runner.FinalizedCheckpoint.Epoch);
            foreach ((ulong Slot, ulong ProposerIndex) proposal in _regenerationProposals.Keys)
            {
                if (proposal.Slot <= finalizedSlot)
                {
                    _regenerationProposals.Remove(proposal);
                }
            }
        }

        if (_importing == RequestedImport.ByRoot)
        {
            if (_byRootRegenerationsThisSlot >= MaxByRootRegenerationsPerSlot)
            {
                if (_logger.IsDebug) _logger.Debug($"Not regenerating the parent state of block {blockRoot} at slot {slot}: this slot's {MaxByRootRegenerationsPerSlot} regenerations for blocks fetched by root are spent");
                LastRefusal = ImportRefusal.RegenerationBudget;
                return BlockImportResult.UnknownParent;
            }

            _byRootRegenerationsThisSlot++;
            return null;
        }

        // A retry of the same block, deferred after its first regeneration, is no repeat proposal.
        string? refusal = _regenerationProposals.TryGetValue((slot, proposerIndex), out Hash256? first) && first != blockRoot ? $"another block of proposer {proposerIndex} at this slot already cost one"
            : _regenerationsThisSlot >= MaxUntrustedRegenerationsPerSlot ? $"this slot's {MaxUntrustedRegenerationsPerSlot} regenerations are spent"
            : null;
        if (refusal is not null)
        {
            // Debug: any cached key can trigger it at will.
            if (_logger.IsDebug) _logger.Debug($"Not regenerating the parent state of block {blockRoot} at slot {slot}: {refusal}");
            LastRefusal = ImportRefusal.RegenerationBudget;
            return BlockImportResult.UnknownParent;
        }

        _regenerationProposals[(slot, proposerIndex)] = blockRoot;
        _regenerationsThisSlot++;
        return null;
    }

    /// <summary>Records a block <c>on_block</c> refused after <see cref="TickToClock"/>, when the ticked store shows the refusal is permanent.</summary>
    /// <remarks>The tick pulls up unrealized finality (specs/phase0/fork-choice.md on_tick), which the checks before the transition never saw; a refusal for any other reason, such as data availability, stays unrecorded.</remarks>
    private void RecordIfRefusalIsPermanent(Hash256 blockRoot, ulong slot, Hash256 parentRoot)
    {
        if (_failedBlocks is not null && CheckBeforeTransition(slot, parentRoot, out bool failedValidation, out _) is not null && failedValidation)
        {
            _failedBlocks.Add(blockRoot, slot);
        }
    }

    /// <summary>Records a block the state transition refused, unless only its proposer signature failed: that signature is not part of the block root, so a forged copy would mark the honest block.</summary>
    private void MarkFailed(Hash256 blockRoot, ulong slot, BeaconStateException failure)
    {
        if (failure is not ProposerSignatureException)
        {
            _failedBlocks?.Add(blockRoot, slot);
        }
    }

    /// <summary>Checks deferred Gloas children against the nearest held ancestor before reserving retry space. Past its lookahead, a direct child uses an advanced state copy; intervening deferred blocks have unknown RANDAO effects and cannot supply that proposer seed.</summary>
    private BlockImportResult DeferForParentPayload(SignedBeaconBlockGloas signedBlock, Hash256 blockRoot, Hash256 ancestorRoot, bool parentDeferred)
    {
        BeaconBlockGloas block = signedBlock.Message!;
        // A full parent whose payload is unverified is a Gloas block: a known Fulu block counts as verified.
        BeaconStateGloas? ancestorState = _states.GetGloasBlockState(ancestorRoot);
        if (ancestorState is null)
        {
            if (RefuseRegeneration(blockRoot, block.Slot, block.ProposerIndex, signedBlock.Signature, _gloasProposerDomain) is { } refused)
            {
                return refused;
            }

            ancestorState = _states.GetOrRegenerateGloasBlockState(ancestorRoot);
        }

        if (ancestorState is null)
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
        catch (BeaconStateException)
        {
            if (_logger.IsDebug) _logger.Debug("Cannot check the proposer with the retained state");
            return BlockImportResult.UnknownParent;
        }

        if (refusal is not null)
        {
            // ethereum/consensus-specs gloas/p2p-interface.md: "[REJECT] The proposer signature is valid" precedes the parent payload check.
            RejectGossip = !parentDeferred && CheckGossipSignature(new ForkedSignedBeaconBlock.OfGloas(signedBlock)) == false;
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

    private BlockImportResult DeferBehindDeferredParent(SignedBeaconBlockGloas signedBlock, Hash256 blockRoot, DeferredBlock parent)
    {
        ulong slot = signedBlock.Message!.Slot;
        string? refusal = CheckBeforeTransition(slot, parent.AncestorRoot, out _, out _) ?? (slot > parent.Slot ? null : $"the block is not after its parent's slot {parent.Slot}");
        if (refusal is not null)
        {
            LastRefusal = slot > parent.Slot ? ImportRefusal.LocalAdmission : ImportRefusal.None;
            if (_logger.IsWarn) _logger.Warn($"Dropping block {blockRoot} at slot {slot} before its state transition: {refusal}");
            return BlockImportResult.Invalid;
        }

        return DeferForParentPayload(signedBlock, blockRoot, parent.AncestorRoot, parentDeferred: true);
    }

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
        catch (BeaconStateException e) when (e.RejectGossip)
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
            bool valid = false;
            // specs/bellatrix/optimistic-sync.md: a VALID newPayload makes the payload and its ancestors VALID; an invalidated payload stays unverified and keeps its verdict.
            if (result == ExecutionPayloadEnvelopeImportResult.Valid && _runner.GetExecutionBlockHash(blockRoot!) is { } payloadHash)
            {
                try
                {
                    valid = _runner.ValidateExecutionChainAndGetPayloadRoot(blockRoot!, payloadHash) == blockRoot;
                }
                catch (ProtoArrayException e)
                {
                    // The payload is recorded either way; a verdict that contradicts an invalid ancestor is the execution layer's fault, not the envelope's.
                    if (_logger.IsError) _logger.Error($"Execution layer reported the payload of {blockRoot} VALID against an invalid ancestor: {e.Message}");
                }
            }

            _store.PutExecutionPayloadEnvelope(blockRoot!, envelope, valid);
            _runner.OnExecutionPayloadVerified(blockRoot!);
        }

        return result;
    }

    /// <inheritdoc/>
    public bool? VerifyEnvelopeSignature(SignedExecutionPayloadEnvelope envelope) => _envelopes.VerifySignature(envelope);

    /// <summary>Advances fork-choice time to the node's clock when the block reached the importer, before <c>on_block</c>, whose proposer boost and <c>record_block_timeliness</c> read <c>store.time</c>.</summary>
    /// <remarks>
    /// fork-choice.md on_block: transition latency does not count as lateness; an early receipt uses its slot start after the clock reaches it.
    /// </remarks>
    private void TickToClock(ulong blockSlot, long receivedMs)
    {
        long slotStartMs = _clock.SlotStartMilliseconds(blockSlot);
        long tickMs = receivedMs < slotStartMs ? Math.Min(_clock.UnixMilliseconds, slotStartMs) : receivedMs;
        ulong time = _runner.GenesisTime + (ulong)Math.Max(0L, tickMs - _clock.SlotStartMilliseconds(0)) / 1000;
        if (time > _runner.Time)
        {
            _runner.OnTick(time);
        }
    }

    /// <summary>Whether neither the node's clock nor fork-choice time has reached <paramref name="slot"/>, so the block must wait for its slot.</summary>
    /// <remarks>fork-choice.md on_block: future blocks wait until the clock or slot tick reaches their slot.</remarks>
    private bool IsBeforeItsSlot(ulong slot, Hash256 blockRoot)
    {
        if (slot <= _runner.CurrentSlot || _clock.UnixMilliseconds >= _clock.SlotStartMilliseconds(slot)) return false;

        if (_logger.IsDebug) _logger.Debug($"Deferring block {blockRoot} at slot {slot} until its slot starts");
        return true;
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

    /// <summary>Spec <c>get_ptc</c> of the post-state of <paramref name="headRoot"/> for <paramref name="slot"/>.</summary>
    /// <returns>The committee's validator indices, or <c>null</c> when the head is not a held Gloas block or the slot is outside its window.</returns>
    internal ulong[]? GetPtc(Hash256 headRoot, ulong slot)
    {
        try
        {
            return _states.GetGloasBlockState(headRoot)?.GetPtc(slot, _spec).Indices;
        }
        catch (BeaconStateException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public ImportRefusal LastRefusal { get; private set; }

    /// <inheritdoc/>
    bool IBlockImporter.RejectGossip => RejectGossip;

    private bool RejectGossip { get; set; }

    /// <inheritdoc/>
    public HeadView ComputeHead()
    {
        ForkChoiceNode headNode = _runner.GetHeadNode();
        Hash256 head = headNode.Root;
        // After GetHeadNode, so the copy carries the weights this head was chosen by.
        ForkChoiceSnapshot? snapshot = _forkChoiceSnapshots is null && _proposerLookaheads is null ? null : _runner.Snapshot();
        _forkChoiceSnapshots?.Current = snapshot;
        AdoptHeadLineage(head);
        PublishProposerLookahead(head, snapshot);
        UpdateCanonicalIndex(head);
        return new HeadView(
            head,
            _runner.GetBlockSlot(head) ?? 0,
            // A pre-Gloas head is EMPTY and has no bid, so it keeps its own payload hash.
            headNode.PayloadStatus == ForkChoicePayloadStatus.Full ? _runner.GetExecutionBlockHash(head) : GetParentBlockHash(head) ?? _runner.GetExecutionBlockHash(head),
            CheckpointExecutionHash(_runner.JustifiedCheckpoint.Root),
            CheckpointExecutionHash(_runner.FinalizedCheckpoint.Root),
            _runner.JustifiedCheckpoint,
            _runner.FinalizedCheckpoint,
            headNode.PayloadStatus == ForkChoicePayloadStatus.Full);
    }

    private Hash256? CheckpointExecutionHash(Hash256 root) => GetParentBlockHash(root) ?? _runner.GetExecutionBlockHash(root);

    private Hash256? GetParentBlockHash(Hash256 root) => _runner.GetParentBlockHash(root) ?? (root == _gloasAnchorRoot ? _gloasAnchorParentBlockHash : null);

    /// <inheritdoc/>
    public void OnForkchoiceUpdated(Hash256 headRoot, Hash256 headExecutionHash, PayloadStatusV1 status)
    {
        if (status.Status == PayloadStatus.Valid)
            ValidateExecutionChain(headRoot, headExecutionHash);
        else if (status.Status == PayloadStatus.Invalid)
            _runner.InvalidateExecutionChain(headRoot, headExecutionHash, status.LatestValidHash);
    }

    private void ValidateExecutionChain(Hash256 root, Hash256 payloadHash)
    {
        if (_runner.ValidateExecutionChainAndGetPayloadRoot(root, payloadHash) is { } payloadRoot)
            _store.SetExecutionPayloadValid(payloadRoot);
    }

    /// <summary>Invalidates <paramref name="blockRoot"/> and its descendants, and the blocks back to <paramref name="latestValidHash"/> when it names a known ancestor.</summary>
    internal void OnInvalidExecutionPayload(Hash256 blockRoot, Hash256? latestValidHash) =>
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
        // Replay after a restart starts from the persisted anchor, which stays behind when the finalized state was not retained;
        // with no anchor, a finalized block fork choice does not hold has no known slot, and the epoch start could be past it.
        _store.PruneExecutionPayloadEnvelopes(_clock.CurrentEpoch, _store.TryGetAnchor(out _, out ulong anchorSlot) ? Math.Min(anchorSlot, finalizedSlot) : _runner.GetBlockSlot(finalized.Root) ?? 0);
        // on_block refuses every block at or below the finalized epoch's start slot, even when that slot is empty.
        ulong neverImportable = Math.Max(finalizedSlot, BeaconStateAccessors.ComputeStartSlotAtEpoch(finalized.Epoch));
        _failedBlocks?.Prune(neverImportable);
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
    public bool? OnGossipAggregate(SignedAggregateAndProof aggregate)
    {
        try
        {
            _runner.OnAggregateAndProof(aggregate);
            return true;
        }
        catch (Exception e)
        {
            Metrics.BeaconChainForkChoiceRejections.Increment(GossipAggregateRejected);
            if (_logger.IsTrace) _logger.Trace($"Rejected gossip aggregate: {e.Message}");
            return IsGossipRejection(e) ? false : null;
        }
    }

    /// <inheritdoc/>
    public bool? OnGossipAggregate(SignedAggregateAndProofGloas aggregate)
    {
        try
        {
            _runner.OnAggregateAndProof(aggregate);
            return true;
        }
        catch (Exception e)
        {
            Metrics.BeaconChainForkChoiceRejections.Increment(GossipAggregateRejected);
            if (_logger.IsTrace) _logger.Trace($"Rejected Gloas gossip aggregate: {e.Message}");
            return IsGossipRejection(e) ? false : null;
        }
    }

    /// <inheritdoc/>
    public bool? OnGossipAttesterSlashing(AttesterSlashing slashing)
    {
        try
        {
            ForkedBeaconState head = RequireSlashableIntersection(slashing.Attestation1!.AttestingIndices!, slashing.Attestation2!.AttestingIndices!);
            _runner.OnAttesterSlashing(slashing, gossipState: head);
            return true;
        }
        catch (Exception e)
        {
            Metrics.BeaconChainForkChoiceRejections.Increment(GossipAttesterSlashingRejected);
            if (_logger.IsTrace) _logger.Trace($"Rejected gossip attester slashing: {e.Message}");
            return IsGossipRejection(e) ? false : null;
        }
    }

    /// <inheritdoc/>
    public bool? OnGossipAttesterSlashing(AttesterSlashingGloas slashing)
    {
        try
        {
            ForkedBeaconState head = RequireSlashableIntersection(slashing.Attestation1!.AttestingIndices!, slashing.Attestation2!.AttestingIndices!);
            _runner.OnAttesterSlashing(slashing, gossipState: head);
            return true;
        }
        catch (Exception e)
        {
            Metrics.BeaconChainForkChoiceRejections.Increment(GossipAttesterSlashingRejected);
            if (_logger.IsTrace) _logger.Trace($"Rejected Gloas gossip attester slashing: {e.Message}");
            return IsGossipRejection(e) ? false : null;
        }
    }

    // p2p-interface.md attester_slashing: an intersecting index must be slashable in the head state before gossip is accepted.
    private ForkedBeaconState RequireSlashableIntersection(ulong[] first, ulong[] second)
    {
        ForkedBeaconState state = _runner.GetHeldHeadState();
        Validator[] validators = state switch
        {
            ForkedBeaconState.OfFulu fulu => fulu.State.Validators!,
            ForkedBeaconState.OfGloas gloas => gloas.State.Validators!,
            _ => throw new NotSupportedException("Unsupported head state"),
        };
        ulong epoch = BeaconStateAccessors.ComputeEpochAtSlot(state.Slot);

        HashSet<ulong> indices = [.. second];
        foreach (ulong index in first)
        {
            if (index < (ulong)validators.Length && indices.Contains(index) && validators[(int)index].IsSlashableValidator(epoch))
            {
                return state;
            }
        }

        // ethereum/consensus-specs p2p-interface.md: "[REJECT] At least one validator in the intersection is slashable".
        throw new ForkChoiceException("Attester slashing has no slashable validator") { RejectGossip = true };
    }

    /// <inheritdoc/>
    public bool? OnGossipPayloadAttestation(PayloadAttestationMessage message)
    {
        // on_payload_attestation_message returns before any check for a vote whose slot is not its block's, recording nothing.
        if (message.Data is not { BeaconBlockRoot: { } blockRoot } data || _runner.GetBlockSlot(blockRoot) != data.Slot)
        {
            return Refuse("it does not name a block fork choice holds at its slot");
        }

        try
        {
            _runner.OnPayloadAttestationMessage(message);
            return true;
        }
        catch (Exception e)
        {
            return Refuse(e.Message, IsGossipRejection(e));
        }

        bool? Refuse(string reason, bool reject = false)
        {
            Metrics.BeaconChainForkChoiceRejections.Increment(GossipPayloadAttestationRejected);
            if (_logger.IsTrace) _logger.Trace($"Rejected gossip payload attestation from validator {message.ValidatorIndex}: {reason}");
            return reject ? false : null;
        }
    }

    private static bool IsGossipRejection(Exception error) =>
        error is ForkChoiceException { RejectGossip: true } or BeaconStateException { RejectGossip: true };

    /// <summary>
    /// The body replay <see cref="ForkChoiceRunner.OnBlock(SignedBeaconBlock, BeaconStateFulu, ExecutionStatus, IDataAvailabilityRule)"/>
    /// leaves to its caller: feeds the block's attestations and attester slashings (already verified
    /// by the transition) to fork choice. A refused operation is counted and skipped, never fatal.
    /// </summary>
    /// <remarks>Each vote is checked as <see cref="ForkChoiceRunner.OnBodyAttestation(Attestation, Hash256)"/> describes.</remarks>
    private void ApplyBodyOperations(BeaconBlockBody body, Hash256 blockRoot)
    {
        foreach (Attestation attestation in body.Attestations!)
        {
            try
            {
                _runner.OnBodyAttestation(attestation, blockRoot);
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

    /// <remarks>Fork choice applies payload attestations during OnBlock; they must not be replayed here.</remarks>
    private void ApplyBodyOperations(BeaconBlockBodyGloas body, Hash256 blockRoot)
    {
        foreach (AttestationGloas attestation in body.Attestations!)
        {
            try
            {
                _runner.OnBodyAttestation(attestation, blockRoot);
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
        // Without a Fulu lineage (a Gloas anchor) there is none to move.
        if (_states.LineageRoot is not { } lineageRoot || _states.LineageState is not { } lineageState
            || head == lineageRoot || _runner.GetBlockSlot(head) is ulong headSlot && SignedBeaconBlockCodec.IsGloasSlot(headSlot, _spec))
        {
            return;
        }

        // A head that fell back to an ancestor (phase0/fork-choice.md filter_block_tree) is no fork: the next block still extends the lineage.
        // An invalid lineage root is not: invalidation also marks every descendant, so no block can extend it again.
        if (_runner.GetBlockExecutionStatus(lineageRoot) != ExecutionStatus.Invalid && IsStrictAncestor(head, lineageRoot))
        {
            if (_logger.IsDebug) _logger.Debug($"Head {head} at slot {_runner.GetBlockSlot(head)} is an ancestor of lineage {lineageRoot}; lineage stays");
            return;
        }

        BeaconStateFulu? headState = _states.GetBlockState(head);
        if (headState is null)
        {
            // Imports building on this head fall back to the fork path until its state is seen again.
            if (_logger.IsWarn) _logger.Warn($"Reorg to {head} whose post-state is not retained; lineage stays at {lineageRoot}");
            return;
        }

        if (_logger.IsInfo) _logger.Info($"Beacon chain reorg: adopting head {head} at slot {_runner.GetBlockSlot(head)} (was {lineageRoot})");
        _states.Retain(lineageRoot, lineageState);
        _states.SetLineage(head, headState.Clone());
        _lineageCache = new EpochCache { Hasher = new CachedBeaconStateHasher() };
        // Unknown here; forces retention at the next epoch advance, which is harmless.
        _lineageBlockStartsEpoch = true;
    }

    private bool IsStrictAncestor(Hash256 ancestor, Hash256 root)
    {
        if (_runner.GetBlockSlot(ancestor) is not ulong ancestorSlot)
        {
            return false;
        }

        foreach (ProtoNode node in _runner.EnumerateAncestors(root))
        {
            if (node.Slot <= ancestorSlot)
            {
                return node.Root == ancestor && root != ancestor;
            }
        }

        return false;
    }

    private void PublishProposerLookahead(Hash256 head, ForkChoiceSnapshot? snapshot)
    {
        if (_proposerLookaheads is null || snapshot is null)
        {
            return;
        }

        (ulong epoch, ulong[]? lookahead) = head == _states.LineageRoot && _states.LineageState is { } lineageState
            ? (lineageState.GetCurrentEpoch(), lineageState.ProposerLookahead)
            : _states.GetGloasBlockState(head) is { } gloasState ? (gloasState.GetCurrentEpoch(), gloasState.ProposerLookahead) : default;
        if (lookahead is null)
        {
            return;
        }

        // The head is a node of the snapshot, so a root is always found.
        Hash256 dependentRoot = ProposerLookaheadSnapshot.FindDependentRoot(snapshot.Nodes, head, BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch))!;
        _proposerLookaheads.Current = new ProposerLookaheadSnapshot(epoch, dependentRoot, lookahead);
    }

    /// <summary>Reconciles the canonical index with head ancestry, removing skipped and higher slots. Stops below the first clean canonical ancestor, caps stale-tail reads at the clock's next slot, and applies changes atomically so interruption preserves the previous index.</summary>
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
            ulong clearTo = childSlot is ulong child ? child - 1 : Math.Max(Math.Min(_canonicalIndexTopSlot, _clock.CurrentSlot + 1), node.Slot);
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

    /// <summary>Captures one import's execution verdict. A shared notifier field could substitute another caller's VALID verdict for SYNCING, which fork choice cannot later invalidate; per-import ownership prevents that race.</summary>
    private sealed class ImportVerdict(IEngineDriver engine) : INewPayloadNotifier
    {
        private BeaconBlockBody? _primed;

        /// <summary>The verdict for this import, optimistic until the hook produces one.</summary>
        public ExecutionStatus Status { get; private set; } = ExecutionStatus.Optimistic;

        public Hash256? LatestValidHash { get; private set; }

        /// <summary>Obtains the verdict for <paramref name="body"/> ahead of the transition that will consume it.</summary>
        /// <remarks>The hook is served this verdict rather than calling the engine a second time.</remarks>
        /// <exception cref="EngineUnavailableException">The execution layer returned no verdict.</exception>
        public void Prime(BeaconBlockBody body)
        {
            Status = engine.NotifyNewPayload(body, out Hash256? latestValidHash);
            LatestValidHash = latestValidHash;
            _primed = body;
        }

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body)
        {
            if (!ReferenceEquals(body, _primed))
            {
                Status = engine.NotifyNewPayload(body, out Hash256? latestValidHash);
                LatestValidHash = latestValidHash;
            }

            return Status;
        }

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
/// <param name="proposerLookaheads">Where every importer publishes its head's proposer lookahead; <c>null</c> publishes nothing.</param>
/// <param name="failedBlocks">Where every importer records the roots of blocks it refused for failing validation; <c>null</c> records nothing.</param>
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
    ForkChoiceSnapshotHolder? forkChoiceSnapshots = null,
    ProposerLookaheadHolder? proposerLookaheads = null,
    FailedBlockRoots? failedBlocks = null) : IBlockImporterFactory
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
            forkChoiceSnapshots,
            proposerLookaheads,
            failedBlocks);
    }

}
