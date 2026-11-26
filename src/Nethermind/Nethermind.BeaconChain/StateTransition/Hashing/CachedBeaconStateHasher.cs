// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Threading;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.BeaconChain.StateTransition.Hashing;

/// <summary>
/// Incremental <see cref="IBeaconStateHasher"/> that snapshot-diffs the big state fields between
/// calls and re-merkleizes only the changed subtrees.
/// </summary>
/// <remarks>
/// <para>
/// Each public call compares the state against the snapshot taken on the previous
/// call. The result is correct for any state whose unchanged content is reference-identical
/// (validators, sync committees, the payload header, and the root vectors — all
/// immutable-by-convention objects) or value-identical (balances, participation, inactivity
/// scores) to the snapshot. This holds both for in-place mutation of the same state instance and
/// for states produced by <see cref="BeaconStateClone.Clone"/>, since clones share element
/// references with their source.
/// </para>
/// <para>
/// Within a single slot-processing loop, hashes after a non-epoch slot update only the slot,
/// latest header, block/state-root leaves and Gloas payload availability. The first slot and
/// every epoch boundary use the full snapshot diff, as do calls outside that loop.
/// </para>
/// <para>
/// Cached per field: validators (per-validator leaf roots plus the tree above, diffed by
/// <see cref="object.ReferenceEquals"/>), balances and inactivity scores (4-ulong chunk diff),
/// both participation lists (32-byte chunk diff), the block/state-root and RANDAO-mix vectors
/// (per-element reference diff), and the sync committees and payload header (whole-object
/// reference memos), and the pending deposit, withdrawal and consolidation queues. The remaining small fields are re-merkleized on every call. When more
/// than half of a tree's chunks are dirty (e.g. the epoch-boundary balance rewrite), the subtree
/// is rebuilt level-by-level in parallel instead of patching paths.
/// </para>
/// <para>
/// <see cref="BeaconStateGloas"/> gets its own caches for the fields EIP-7916 retypes to
/// <c>ProgressiveList</c> (validators, balances, participation, inactivity scores) and for the
/// builder registry, all held as progressive trees, plus a per-element reference diff for
/// <c>ptc_window</c>. The root vectors and sync committees are shared by both forks' calls, which
/// is safe because every cache diffs against its own snapshot.
/// </para>
/// <para>
/// Use one instance per followed state lineage, like the rest of <see cref="EpochCache"/>.
/// Hashing a sibling state after a reorg stays correct; only the first call pays for the larger diff.
/// Not thread-safe.
/// </para>
/// </remarks>
public sealed class CachedBeaconStateHasher : IBeaconStateHasher
{
    // Tree depths follow the generated BeaconStateFulu.Merkleize chunk limits:
    // lists of uint64 / byte with limit 2^40 have 2^38 / 2^35 chunks, the validator registry has
    // one chunk per validator with limit 2^40, and the root vectors are exact powers of two.
    private const int ValidatorsDepth = 40;
    private const ulong HistoricalRootsLimit = 16_777_216;
    private const int UInt64ListDepth = 38;
    private const int ParticipationDepth = 35;
    private const int RootsVectorDepth = Presets.SlotsPerHistoricalRoot == 64 ? 6 : 13;
    private const int RandaoMixesDepth = Presets.EpochsPerHistoricalVector == 64 ? 6 : 16;
    private const ulong SlashingsChunkCount = Presets.EpochsPerSlashingsVector / 4;
    private static readonly ulong ProposerLookaheadChunkCount = Presets.ProposerLookaheadSlots / 4;
    private const int JustificationBitsLength = Presets.JustificationBitsLength;
    private const int FieldCount = 38;
    private const int GloasFieldCount = 46;
    private const int ParallelLeafThreshold = 2048;

    private static readonly Fork s_defaultFork = new();
    private static readonly BeaconBlockHeader s_defaultLatestBlockHeader = new();
    private static readonly Eth1Data s_defaultEth1Data = new();
    private static readonly Checkpoint s_defaultCheckpoint = new();
    private static readonly BitArray s_defaultJustificationBits = new(4);
    private static readonly BitArray s_defaultExecutionPayloadAvailability = new((int)Presets.SlotsPerHistoricalRoot);
    // EIP-7495 active_fields of the Gloas BeaconState: all 46 fields are present.
    private static readonly byte[] s_gloasActiveFields = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x3F];

    private readonly ImmutableListRoot<Hash256> _historicalRoots = new(RootListRoot);
    private readonly ImmutableListRoot<HistoricalSummary> _historicalSummaries = new(static items =>
    {
        HistoricalSummary.MerkleizeList(items, HistoricalRootsLimit, out UInt256 root);
        return root;
    });
    private readonly ImmutableListRoot<BuilderPendingWithdrawal> _builderPendingWithdrawals = new(static items =>
    {
        BuilderPendingWithdrawal.MerkleizeProgressiveList(items, out UInt256 root);
        return root;
    });
    private readonly ContainerListCache<PendingDeposit> _pendingDeposits = new(new BoundedChunkTree(27));
    private readonly ContainerListCache<PendingDeposit> _gloasPendingDeposits = new(new ProgressiveChunkTree());
    private readonly ContainerListCache<PendingPartialWithdrawal> _pendingPartialWithdrawals = new(new BoundedChunkTree(BitOperations.Log2((uint)Presets.PendingPartialWithdrawalsLimit)));
    private readonly ContainerListCache<PendingPartialWithdrawal> _gloasPendingPartialWithdrawals = new(new ProgressiveChunkTree());
    private readonly ContainerListCache<PendingConsolidation> _pendingConsolidations = new(new BoundedChunkTree(BitOperations.Log2((uint)Presets.PendingConsolidationsLimit)));
    private readonly ContainerListCache<PendingConsolidation> _gloasPendingConsolidations = new(new ProgressiveChunkTree());
    private readonly ContainerListCache<Validator> _validators = new(new BoundedChunkTree(ValidatorsDepth));
    private readonly BasicListCache _balances = new(new BoundedChunkTree(UInt64ListDepth));
    private readonly BasicListCache _inactivityScores = new(new BoundedChunkTree(UInt64ListDepth));
    private readonly BasicListCache _previousEpochParticipation = new(new BoundedChunkTree(ParticipationDepth));
    private readonly BasicListCache _currentEpochParticipation = new(new BoundedChunkTree(ParticipationDepth));
    private readonly HashVectorCache _blockRoots = new(RootsVectorDepth);
    private readonly HashVectorCache _stateRoots = new(RootsVectorDepth);
    private readonly HashVectorCache _randaoMixes = new(RandaoMixesDepth);
    private readonly ContainerMemo<SyncCommittee> _currentSyncCommittee = new();
    private readonly ContainerMemo<SyncCommittee> _nextSyncCommittee = new();
    private readonly ContainerMemo<ExecutionPayloadHeader> _latestExecutionPayloadHeader = new();
    private readonly UInt256[] _fieldRoots = new UInt256[FieldCount];

    private readonly ContainerListCache<Validator> _gloasValidators = new(new ProgressiveChunkTree());
    private readonly BasicListCache _gloasBalances = new(new ProgressiveChunkTree());
    private readonly BasicListCache _gloasInactivityScores = new(new ProgressiveChunkTree());
    private readonly BasicListCache _gloasPreviousEpochParticipation = new(new ProgressiveChunkTree());
    private readonly BasicListCache _gloasCurrentEpochParticipation = new(new ProgressiveChunkTree());
    private readonly ContainerListCache<Builder> _builders = new(new ProgressiveChunkTree());
    private readonly ContainerVectorCache<PayloadTimelinessCommittee> _ptcWindow = new((int)Presets.PtcWindowLength);
    private readonly UInt256[] _gloasFieldRoots = new UInt256[GloasFieldCount];
    private bool _fuluCachesLive;

    private static readonly BlockHashChanges s_unchangedLists = new();
    private readonly WeakReference<BeaconStateFulu> _verifiedPostState = new(null!);
    private readonly WeakReference<BeaconStateFulu> _importClone = new(null!);
    private Hash256? _verifiedStateRoot;
    private Hash256? _admittedBlockRoot;

    internal void RecordVerifiedPostState(BeaconStateFulu state, Hash256 stateRoot)
    {
        _verifiedPostState.SetTarget(state);
        _verifiedStateRoot = stateRoot;
    }

    internal void AdmitBlock(BeaconStateFulu state, Hash256 blockRoot)
    {
        if (_verifiedStateRoot is not null && _verifiedPostState.TryGetTarget(out BeaconStateFulu? verified) && ReferenceEquals(verified, state))
            _admittedBlockRoot = blockRoot;
    }

    internal void RecordImportClone(BeaconStateFulu parent, BeaconStateFulu clone, Hash256 parentRoot)
    {
        if (!parent.ExposedForApi && _admittedBlockRoot == parentRoot &&
            _verifiedPostState.TryGetTarget(out BeaconStateFulu? verified) && ReferenceEquals(verified, parent))
            _importClone.SetTarget(clone);
    }

    internal Hash256 HashTreeRootBeforeSlot(BeaconStateFulu state)
    {
        if (_verifiedStateRoot is { } expected && _importClone.TryGetTarget(out BeaconStateFulu? clone) && ReferenceEquals(clone, state) &&
            _verifiedPostState.TryGetTarget(out BeaconStateFulu? parent) && !parent.ExposedForApi)
        {
            // Both arrays came from the verified parent. Recheck fields shared with other state readers.
            Hash256 actual = HashTreeRoot(state, s_unchangedLists);
            if (actual == expected)
                return actual;
        }
        return HashTreeRoot(state);
    }

    private void InvalidateVerifiedPostState()
    {
        _verifiedStateRoot = _admittedBlockRoot = null;
        _importClone.SetTarget(null!);
    }

    private readonly WeakReference<BeaconStateFulu> _slotBaseline = new(null!);
    private readonly BlockHashChanges _blockChanges = new();
    private ulong _baselineSlot;

    internal void RecordSlotBaseline(BeaconStateFulu state)
    {
        _slotBaseline.SetTarget(state);
        _baselineSlot = state.Slot;
    }

    /// <summary>Starts recording validator and balance writes on a private import clone with a matching slot snapshot.</summary>
    /// <remarks>Epoch processing runs after the last slot snapshot at a boundary. The next root must diff both lists.</remarks>
    internal bool TryBeginBlock(BeaconStateFulu state)
    {
        if (state.Slot % Presets.SlotsPerEpoch == 0 || _baselineSlot + 1 != state.Slot ||
            !_slotBaseline.TryGetTarget(out BeaconStateFulu? baseline) || !ReferenceEquals(baseline, state))
            return false;
        _blockChanges.Validators.Count = _blockChanges.Balances.Count = 0;
        state.BlockChanges = _blockChanges;
        return true;
    }

    internal void EndBlock(BeaconStateFulu state)
    {
        state.BlockChanges = null;
        _slotBaseline.SetTarget(null!);
    }

    internal Hash256 HashTreeRootAfterBlock(BeaconStateFulu state) =>
        HashTreeRoot(state, _slotBaseline.TryGetTarget(out BeaconStateFulu? target) && ReferenceEquals(target, state) ? state.BlockChanges : null);

    /// <summary>Hashes the mutations of one completed non-epoch slot within an uninterrupted slot-processing loop.</summary>
    /// <remarks>The loop must first hash this state normally and must use the full diff after epoch processing.</remarks>
    internal Hash256 HashTreeRootAfterSlot(BeaconStateFulu state)
    {
        UpdateSlotRoots(_fieldRoots, state.Slot, state.LatestBlockHeader!, state.BlockRoots!, state.StateRoots!);
        Merkle.Merkleize(out UInt256 root, _fieldRoots);
        return new Hash256(root.ToLittleEndian());
    }

    /// <inheritdoc cref="HashTreeRootAfterSlot(BeaconStateFulu)"/>
    internal Hash256 HashTreeRootAfterSlot(BeaconStateGloas state)
    {
        UpdateSlotRoots(_gloasFieldRoots, state.Slot, state.LatestBlockHeader!, state.BlockRoots!, state.StateRoots!);
        Merkle.Merkleize(out _gloasFieldRoots[40], state.ExecutionPayloadAvailability!);
        Merkle.MerkleizeProgressive(out UInt256 root, _gloasFieldRoots);
        Merkle.MixInActiveFields(ref root, s_gloasActiveFields);
        return new Hash256(root.ToLittleEndian());
    }

    private void UpdateSlotRoots(UInt256[] roots, ulong slot, BeaconBlockHeader header, Hash256[] blockRoots, Hash256[] stateRoots)
    {
        roots[2] = new UInt256(slot);
        BeaconBlockHeader.Merkleize(header, out roots[4]);
        int previousSlotIndex = (int)((slot - 1) % Presets.SlotsPerHistoricalRoot);
        roots[5] = _blockRoots.RootAfterChange(blockRoots, previousSlotIndex);
        roots[6] = _stateRoots.RootAfterChange(stateRoots, previousSlotIndex);
    }

    /// <inheritdoc/>
    public Hash256 HashTreeRoot(BeaconStateFulu state) => HashTreeRoot(state, null);

    private Hash256 HashTreeRoot(BeaconStateFulu state, BlockHashChanges? changes)
    {
        InvalidateVerifiedPostState();
        _slotBaseline.SetTarget(null!);
        // The generated Merkleize rejects this before computing anything; keep the same contract.
        if (state.LatestExecutionPayloadHeader is null)
            throw new InvalidDataException($"Invalid SSZ value for {nameof(BeaconStateFulu)}.{nameof(state.LatestExecutionPayloadHeader)}: null variable fields are not decodable.");
        ValidateFixedVectors(nameof(BeaconStateFulu), state.Slashings, state.JustificationBits, state.ProposerLookahead);

        _fuluCachesLive = true;
        UInt256[] roots = _fieldRoots;
        roots[0] = new UInt256(state.GenesisTime);
        roots[1] = state.GenesisValidatorsRoot is null ? default : new UInt256(state.GenesisValidatorsRoot.Bytes);
        roots[2] = new UInt256(state.Slot);
        Fork.Merkleize(state.Fork ?? s_defaultFork, out roots[3]);
        // LatestBlockHeader.StateRoot is written in place by ProcessSlot, so it cannot be memoized
        // by reference; it is tiny, recompute it.
        BeaconBlockHeader.Merkleize(state.LatestBlockHeader ?? s_defaultLatestBlockHeader, out roots[4]);
        roots[5] = _blockRoots.Root(state.BlockRoots);
        roots[6] = _stateRoots.Root(state.StateRoots);
        roots[7] = _historicalRoots.Root(state.HistoricalRoots);
        Eth1Data.Merkleize(state.Eth1Data ?? s_defaultEth1Data, out roots[8]);
        roots[9] = state.VoteList.HashTreeRoot();
        roots[10] = new UInt256(state.Eth1DepositIndex);
        Validator[]? validators = state.ValidatorBuffer?.Items;
        roots[11] = changes is null || validators is null ? _validators.Root(validators)
            : _validators.RootAfterChanges(validators, changes.Validators, roots[11]);
        ReadOnlySpan<byte> balances = MemoryMarshal.AsBytes<ulong>(state.Balances);
        roots[12] = changes is null ? _balances.Root(balances, state.Balances?.Length ?? 0)
            : _balances.RootAfterChanges(balances, changes.Balances, roots[12]);
        roots[13] = _randaoMixes.Root(state.RandaoMixes);
        Merkle.Merkleize(out roots[14], MemoryMarshal.AsBytes<ulong>(state.Slashings), SlashingsChunkCount);
        roots[15] = _previousEpochParticipation.Root(state.PreviousEpochParticipation, state.PreviousEpochParticipation?.Length ?? 0);
        roots[16] = _currentEpochParticipation.Root(state.CurrentEpochParticipation, state.CurrentEpochParticipation?.Length ?? 0);
        Merkle.Merkleize(out roots[17], state.JustificationBits ?? s_defaultJustificationBits);
        Checkpoint.Merkleize(state.PreviousJustifiedCheckpoint ?? s_defaultCheckpoint, out roots[18]);
        Checkpoint.Merkleize(state.CurrentJustifiedCheckpoint ?? s_defaultCheckpoint, out roots[19]);
        Checkpoint.Merkleize(state.FinalizedCheckpoint ?? s_defaultCheckpoint, out roots[20]);
        roots[21] = _inactivityScores.Root(MemoryMarshal.AsBytes<ulong>(state.InactivityScoreBuffer?.Items), state.InactivityScoreBuffer?.Items.Length ?? 0);
        roots[22] = _currentSyncCommittee.Root(state.CurrentSyncCommittee);
        roots[23] = _nextSyncCommittee.Root(state.NextSyncCommittee);
        roots[24] = _latestExecutionPayloadHeader.Root(state.LatestExecutionPayloadHeader);
        roots[25] = new UInt256(state.NextWithdrawalIndex);
        roots[26] = new UInt256(state.NextWithdrawalValidatorIndex);
        roots[27] = _historicalSummaries.Root(state.HistoricalSummaries);
        roots[28] = new UInt256(state.DepositRequestsStartIndex);
        roots[29] = new UInt256(state.DepositBalanceToConsume);
        roots[30] = new UInt256(state.ExitBalanceToConsume);
        roots[31] = new UInt256(state.EarliestExitEpoch);
        roots[32] = new UInt256(state.ConsolidationBalanceToConsume);
        roots[33] = new UInt256(state.EarliestConsolidationEpoch);
        roots[34] = _pendingDeposits.ImmutableRoot(state.PendingDeposits);
        roots[35] = _pendingPartialWithdrawals.ImmutableRoot(state.PendingPartialWithdrawals);
        roots[36] = _pendingConsolidations.ImmutableRoot(state.PendingConsolidations);
        Merkle.Merkleize(out roots[37], MemoryMarshal.AsBytes<ulong>(state.ProposerLookahead), ProposerLookaheadChunkCount);

        Merkle.Merkleize(out UInt256 root, roots);
        return new Hash256(root.ToLittleEndian());
    }

    /// <inheritdoc/>
    public Hash256 HashTreeRoot(BeaconStateGloas state)
    {
        _slotBaseline.SetTarget(null!);
        InvalidateVerifiedPostState();
        ValidateFixedVectors(nameof(BeaconStateGloas), state.Slashings, state.JustificationBits, state.ProposerLookahead);
        if (state.BuilderPendingPayments?.Length != (int)Presets.BuilderPendingPaymentsLength)
            throw new InvalidDataException($"Invalid SSZ value for {nameof(BeaconStateGloas)}.{nameof(state.BuilderPendingPayments)}: expected {Presets.BuilderPendingPaymentsLength} elements but found {state.BuilderPendingPayments?.Length ?? 0}.");
        if (state.LatestExecutionPayloadBid is null)
            throw new InvalidDataException($"Invalid SSZ value for {nameof(BeaconStateGloas)}.{nameof(state.LatestExecutionPayloadBid)}: null variable fields are not decodable.");
        if (state.ExecutionPayloadAvailability is not null && state.ExecutionPayloadAvailability.Length != (int)Presets.SlotsPerHistoricalRoot)
            throw new InvalidDataException($"Invalid SSZ value for {nameof(BeaconStateGloas)}.{nameof(state.ExecutionPayloadAvailability)}: expected {Presets.SlotsPerHistoricalRoot} bits but found {state.ExecutionPayloadAvailability.Length}.");

        // Gloas hashing does not use the Fulu-only trees; release their cached data.
        if (_fuluCachesLive)
        {
            _pendingDeposits.Reset();
            _pendingPartialWithdrawals.Reset();
            _pendingConsolidations.Reset();
            _validators.Reset();
            _balances.Reset();
            _inactivityScores.Reset();
            _previousEpochParticipation.Reset();
            _currentEpochParticipation.Reset();
            _fuluCachesLive = false;
        }

        UInt256[] roots = _gloasFieldRoots;
        roots[0] = new UInt256(state.GenesisTime);
        roots[1] = state.GenesisValidatorsRoot is null ? default : new UInt256(state.GenesisValidatorsRoot.Bytes);
        roots[2] = new UInt256(state.Slot);
        Fork.Merkleize(state.Fork ?? s_defaultFork, out roots[3]);
        BeaconBlockHeader.Merkleize(state.LatestBlockHeader ?? s_defaultLatestBlockHeader, out roots[4]);
        roots[5] = _blockRoots.Root(state.BlockRoots);
        roots[6] = _stateRoots.Root(state.StateRoots);
        roots[7] = _historicalRoots.Root(state.HistoricalRoots);
        Eth1Data.Merkleize(state.Eth1Data ?? s_defaultEth1Data, out roots[8]);
        roots[9] = state.VoteList.HashTreeRoot();
        roots[10] = new UInt256(state.Eth1DepositIndex);
        roots[11] = _gloasValidators.Root(state.ValidatorBuffer?.Items);
        roots[12] = _gloasBalances.Root(MemoryMarshal.AsBytes<ulong>(state.Balances), state.Balances?.Length ?? 0);
        roots[13] = _randaoMixes.Root(state.RandaoMixes);
        Merkle.Merkleize(out roots[14], MemoryMarshal.AsBytes<ulong>(state.Slashings), SlashingsChunkCount);
        roots[15] = _gloasPreviousEpochParticipation.Root(state.PreviousEpochParticipation, state.PreviousEpochParticipation?.Length ?? 0);
        roots[16] = _gloasCurrentEpochParticipation.Root(state.CurrentEpochParticipation, state.CurrentEpochParticipation?.Length ?? 0);
        Merkle.Merkleize(out roots[17], state.JustificationBits ?? s_defaultJustificationBits);
        Checkpoint.Merkleize(state.PreviousJustifiedCheckpoint ?? s_defaultCheckpoint, out roots[18]);
        Checkpoint.Merkleize(state.CurrentJustifiedCheckpoint ?? s_defaultCheckpoint, out roots[19]);
        Checkpoint.Merkleize(state.FinalizedCheckpoint ?? s_defaultCheckpoint, out roots[20]);
        roots[21] = _gloasInactivityScores.Root(MemoryMarshal.AsBytes<ulong>(state.InactivityScoreBuffer?.Items), state.InactivityScoreBuffer?.Items.Length ?? 0);
        roots[22] = _currentSyncCommittee.Root(state.CurrentSyncCommittee);
        roots[23] = _nextSyncCommittee.Root(state.NextSyncCommittee);
        roots[24] = state.LatestBlockHash is null ? default : new UInt256(state.LatestBlockHash.Bytes);
        roots[25] = new UInt256(state.NextWithdrawalIndex);
        roots[26] = new UInt256(state.NextWithdrawalValidatorIndex);
        roots[27] = _historicalSummaries.Root(state.HistoricalSummaries);
        roots[28] = new UInt256(state.DepositRequestsStartIndex);
        roots[29] = new UInt256(state.DepositBalanceToConsume);
        roots[30] = new UInt256(state.ExitBalanceToConsume);
        roots[31] = new UInt256(state.EarliestExitEpoch);
        roots[32] = new UInt256(state.ConsolidationBalanceToConsume);
        roots[33] = new UInt256(state.EarliestConsolidationEpoch);
        roots[34] = _gloasPendingDeposits.ImmutableRoot(state.PendingDeposits);
        roots[35] = _gloasPendingPartialWithdrawals.ImmutableRoot(state.PendingPartialWithdrawals);
        roots[36] = _gloasPendingConsolidations.ImmutableRoot(state.PendingConsolidations);
        Merkle.Merkleize(out roots[37], MemoryMarshal.AsBytes<ulong>(state.ProposerLookahead), ProposerLookaheadChunkCount);
        roots[38] = _builders.Root(state.Builders);
        roots[39] = new UInt256(state.NextWithdrawalBuilderIndex);
        Merkle.Merkleize(out roots[40], state.ExecutionPayloadAvailability ?? s_defaultExecutionPayloadAvailability);
        BuilderPendingPayment.MerkleizeVector(state.BuilderPendingPayments, out roots[41]);
        roots[42] = _builderPendingWithdrawals.Root(state.BuilderPendingWithdrawals);
        ExecutionPayloadBid.Merkleize(state.LatestExecutionPayloadBid, out roots[43]);
        SszWithdrawal.MerkleizeProgressiveList(state.PayloadExpectedWithdrawals ?? [], out roots[44]);
        roots[45] = _ptcWindow.Root(state.PtcWindow);

        Merkle.MerkleizeProgressive(out UInt256 root, roots);
        Merkle.MixInActiveFields(ref root, s_gloasActiveFields);
        return new Hash256(root.ToLittleEndian());
    }

    /// <summary>Rejects a wrong-length fixed vector like the generated hasher; null hashes as the default vector.</summary>
    private static void ValidateFixedVectors(string stateType, ulong[]? slashings, BitArray? justificationBits, ulong[]? proposerLookahead)
    {
        if (slashings is not null && slashings.Length != (int)Presets.EpochsPerSlashingsVector)
            ThrowInvalidVector(stateType, nameof(BeaconStateFulu.Slashings), "elements", (int)Presets.EpochsPerSlashingsVector, slashings.Length);
        if (justificationBits is not null && justificationBits.Length != JustificationBitsLength)
            ThrowInvalidVector(stateType, nameof(BeaconStateFulu.JustificationBits), "bits", JustificationBitsLength, justificationBits.Length);
        if (proposerLookahead is not null && proposerLookahead.Length != (int)Presets.ProposerLookaheadSlots)
            ThrowInvalidVector(stateType, nameof(BeaconStateFulu.ProposerLookahead), "elements", (int)Presets.ProposerLookaheadSlots, proposerLookahead.Length);
    }

    private static void ThrowInvalidVector(string stateType, string field, string unit, int expected, int actual) =>
        throw new InvalidDataException($"Invalid SSZ value for {stateType}.{field}: expected {expected} {unit} but found {actual}.");

    /// <summary>Computes the root of a <c>List[Root, limit]</c> (small on mainnet: frozen since Capella).</summary>
    private static UInt256 RootListRoot(Hash256[] hashes)
    {
        int count = hashes.Length;
        UInt256[] chunks = ArrayPool<UInt256>.Shared.Rent(count);
        for (int i = 0; i < count; i++)
        {
            chunks[i] = new UInt256(hashes[i].Bytes);
        }
        Merkle.Merkleize(out UInt256 root, chunks.AsSpan(0, count), HistoricalRootsLimit);
        ArrayPool<UInt256>.Shared.Return(chunks);
        Merkle.MixIn(ref root, count);
        return root;
    }

    private sealed class ImmutableListRoot<T>(Func<T[], UInt256> compute)
    {
        private T[]? _items;
        private UInt256 _root;

        public UInt256 Root(T[]? items)
        {
            items ??= [];
            if (!ReferenceEquals(items, _items))
            {
                _root = compute(items);
                _items = items;
            }
            return _root;
        }
    }

    /// <summary>
    /// Caches the root of a container field that the state transition replaces wholesale and never
    /// mutates in place (sync committees, the latest execution payload header), keyed by reference.
    /// </summary>
    private sealed class ContainerMemo<T> where T : class, ISszCodec<T>, new()
    {
        private static readonly T s_default = new();
        private T? _last;
        private UInt256 _root;
        private bool _cached;

        public UInt256 Root(T? value)
        {
            if (!_cached || !ReferenceEquals(value, _last))
            {
                // A null field merkleizes as the default container, like the generated code.
                T.Merkleize(value ?? s_default, out _root);
                _last = value;
                _cached = true;
            }
            return _root;
        }
    }

    /// <summary>
    /// Caches a list of containers (the validator and builder registries): per-element leaf roots
    /// plus the tree above, diffed by element reference against the previous call (elements are
    /// immutable-by-convention, so a mutation is always a replaced instance).
    /// </summary>
    private sealed class ContainerListCache<T>(IChunkTree tree) where T : class, ISszCodec<T>
    {
        private readonly IChunkTree _tree = tree;
        private T?[] _snapshot = [];
        private int _count;

        private T[]? _immutableItems;
        private UInt256 _immutableRoot;

        /// <summary>Reuses the root of a state field whose array and elements are immutable once assigned.</summary>
        public UInt256 ImmutableRoot(T[]? items)
        {
            items ??= [];
            if (ReferenceEquals(items, _immutableItems)) return _immutableRoot;
            _immutableRoot = Root(items);
            _immutableItems = items;
            return _immutableRoot;
        }

        public UInt256 Root(T[]? items)
        {
            int count = items?.Length ?? 0;
            bool shrunk = count < _count;
            if (shrunk)
            {
                Array.Clear(_snapshot, count, _count - count);
                _count = 0;
            }
            _tree.SetLeafCount(count);
            if (_snapshot.Length < count)
                Array.Resize(ref _snapshot, Math.Max(count, _snapshot.Length * 2));

            int[] dirty = ArrayPool<int>.Shared.Rent(Math.Max(1, count));
            int dirtyCount = 0;
            for (int i = 0; i < count; i++)
            {
                T item = items![i];
                if (i < _count && ReferenceEquals(item, _snapshot[i]))
                    continue;
                _snapshot[i] = item;
                dirty[dirtyCount++] = i;
            }
            _count = count;

            if (dirtyCount >= ParallelLeafThreshold)
            {
                int[] dirtyLocal = dirty;
                T[] itemsLocal = items!;
                IChunkTree treeLocal = _tree;
                BeaconParallel.For(0, dirtyCount, r =>
                {
                    int i = dirtyLocal[r];
                    T.Merkleize(itemsLocal[i], out treeLocal.Leaf(i));
                });
            }
            else
                for (int r = 0; r < dirtyCount; r++)
                {
                    int i = dirty[r];
                    T.Merkleize(items![i], out _tree.Leaf(i));
                }

            if (shrunk || dirtyCount * 2 > count)
                _tree.Rebuild();
            else if (dirtyCount > 0)
                _tree.Update(dirty, dirtyCount);
            ArrayPool<int>.Shared.Return(dirty);

            UInt256 root = _tree.Root;
            Merkle.MixIn(ref root, count);
            return root;
        }

        public UInt256 RootAfterChanges(T[] items, BlockHashChanges.Indices dirty, UInt256 previous)
        {
            if (items.Length != _count || dirty.Count * 2 > _count)
                return Root(items);
            if (dirty.Count == 0)
                return previous;
            dirty.Sort();
            int count = 0;
            try
            {
                for (int i = 0; i < dirty.Count; i++)
                {
                    int index = dirty.Items[i];
                    if (ReferenceEquals(_snapshot[index], items[index])) continue;
                    T.Merkleize(items[index], out _tree.Leaf(index));
                    _snapshot[index] = items[index];
                    dirty.Items[count++] = index;
                }
                _tree.Update(dirty.Items, count);
            }
            catch
            {
                Reset();
                throw;
            }
            UInt256 root = _tree.Root;
            Merkle.MixIn(ref root, _count);
            return root;
        }

        public void Reset()
        {
            _immutableItems = null;
            _tree.Reset();
            _snapshot = [];
            _count = 0;
        }
    }

    /// <summary>
    /// Caches a basic-type SSZ list (uint64 balances/inactivity scores, byte participation):
    /// a value snapshot diffed per 32-byte chunk, with dirty-path updates.
    /// </summary>
    private sealed class BasicListCache(IChunkTree tree)
    {
        private readonly IChunkTree _tree = tree;
        private byte[] _snapshot = [];
        private int _snapshotLength;

        public UInt256 Root(ReadOnlySpan<byte> data, int elementCount)
        {
            int chunkCount = (data.Length + 31) >> 5;
            int alignedLength = chunkCount << 5;
            int oldChunkCount = (_snapshotLength + 31) >> 5;
            bool shrunk = data.Length < _snapshotLength;
            if (shrunk)
                oldChunkCount = 0; // Stale boundary nodes: rediff everything and rebuild below.

            _tree.SetLeafCount(chunkCount);
            if (_snapshot.Length < alignedLength)
                Array.Resize(ref _snapshot, Math.Max(alignedLength, _snapshot.Length * 2));

            int fullChunks = data.Length >> 5;
            ReadOnlySpan<UInt256> dataChunks = MemoryMarshal.Cast<byte, UInt256>(data[..(fullChunks << 5)]);
            Span<UInt256> snapshotChunks = MemoryMarshal.Cast<byte, UInt256>(_snapshot.AsSpan(0, alignedLength));

            int[] dirty = ArrayPool<int>.Shared.Rent(Math.Max(1, chunkCount));
            int dirtyCount = 0;
            for (int i = 0; i < fullChunks; i++)
            {
                // Appended chunks (i >= oldChunkCount) are always dirty, even when zero, so the
                // zero-initialized interior nodes the tree grew get computed.
                if (i < oldChunkCount && dataChunks[i] == snapshotChunks[i])
                    continue;
                snapshotChunks[i] = dataChunks[i];
                _tree.Leaf(i) = dataChunks[i];
                dirty[dirtyCount++] = i;
            }
            if (fullChunks < chunkCount)
            {
                Span<byte> padded = stackalloc byte[32];
                padded.Clear();
                data[(fullChunks << 5)..].CopyTo(padded);
                UInt256 chunk = new(padded);
                if (fullChunks >= oldChunkCount || chunk != snapshotChunks[fullChunks])
                {
                    snapshotChunks[fullChunks] = chunk;
                    _tree.Leaf(fullChunks) = chunk;
                    dirty[dirtyCount++] = fullChunks;
                }
            }
            _snapshotLength = data.Length;

            if (shrunk || dirtyCount * 2 > chunkCount)
                _tree.Rebuild();
            else if (dirtyCount > 0)
                _tree.Update(dirty, dirtyCount);
            ArrayPool<int>.Shared.Return(dirty);

            UInt256 root = _tree.Root;
            Merkle.MixIn(ref root, elementCount);
            return root;
        }

        public UInt256 RootAfterChanges(ReadOnlySpan<byte> data, BlockHashChanges.Indices dirty, UInt256 previous)
        {
            int elementCount = data.Length / sizeof(ulong);
            if (data.Length != _snapshotLength || dirty.Count * 2 > (data.Length + 31) / 32)
                return Root(data, elementCount);
            if (dirty.Count == 0)
                return previous;
            dirty.Sort();
            int count = 0;
            for (int i = 0; i < dirty.Count; i++)
            {
                int chunk = dirty.Items[i];
                int offset = chunk * 32;
                Span<byte> snapshot = _snapshot.AsSpan(offset, 32);
                ReadOnlySpan<byte> source = data.Slice(offset, Math.Min(32, data.Length - offset));
                if (source.SequenceEqual(snapshot[..source.Length])) continue;
                source.CopyTo(snapshot);
                _tree.Leaf(chunk) = new UInt256(snapshot);
                dirty.Items[count++] = chunk;
            }
            _tree.Update(dirty.Items, count);
            UInt256 root = _tree.Root;
            Merkle.MixIn(ref root, elementCount);
            return root;
        }

        public void Reset()
        {
            _tree.Reset();
            _snapshot = [];
            _snapshotLength = 0;
        }
    }

    /// <summary>
    /// Caches a <c>Vector[Root, 2^depth]</c> (block roots, state roots, RANDAO mixes): element
    /// roots are the raw 32 bytes, diffed by element reference (<see cref="Hash256"/> instances
    /// are immutable and assigned, never rewritten).
    /// </summary>
    private sealed class HashVectorCache(int depth)
    {
        private readonly MerkleChunkTree _tree = new(depth);
        private Hash256?[] _snapshot = [];
        private readonly int[] _singleDirtyIndex = new int[1];

        public UInt256 RootAfterChange(Hash256[] vector, int index)
        {
            Hash256 element = vector[index];
            _snapshot[index] = element;
            _tree.SetLeafCount(vector.Length)[index] = new UInt256(element.Bytes);
            _singleDirtyIndex[0] = index;
            _tree.Update(_singleDirtyIndex, 1);
            return _tree.Root;
        }

        public UInt256 Root(Hash256[]? vector)
        {
            if (vector is null)
                return Merkle.ZeroHashes[depth]; // The generated default-vector root.
            if (vector.Length != 1 << depth)
                throw new InvalidDataException($"Expected an SSZ vector of {1 << depth} roots, got {vector.Length}");

            UInt256[] leaves = _tree.SetLeafCount(vector.Length);
            bool initial = _snapshot.Length == 0;
            if (initial)
                _snapshot = new Hash256?[vector.Length];

            int[] dirty = ArrayPool<int>.Shared.Rent(vector.Length);
            int dirtyCount = 0;
            for (int i = 0; i < vector.Length; i++)
            {
                Hash256? element = vector[i];
                if (!initial && ReferenceEquals(element, _snapshot[i]))
                    continue;
                if (element is null)
                {
                    // Earlier leaves already changed without a tree update; drop them so the next call rebuilds.
                    ArrayPool<int>.Shared.Return(dirty);
                    Reset();
                    ThrowNullRoot(i);
                }
                _snapshot[i] = element;
                leaves[i] = new UInt256(element.Bytes);
                dirty[dirtyCount++] = i;
            }

            if (initial || dirtyCount * 2 > vector.Length)
                _tree.Rebuild();
            else if (dirtyCount > 0)
                _tree.Update(dirty, dirtyCount);
            ArrayPool<int>.Shared.Return(dirty);

            return _tree.Root;
        }

        // Matches the generated hasher, which dereferences a null root element.
        [DoesNotReturn]
        private static void ThrowNullRoot(int index) =>
            throw new NullReferenceException($"SSZ root vector element {index} is null.");

        public void Reset()
        {
            _tree.Reset();
            _snapshot = [];
        }
    }

    /// <summary>
    /// Caches a <c>Vector[T, length]</c> whose elements are replaced, never mutated in place
    /// (<c>ptc_window</c>): per-element roots diffed by element reference.
    /// </summary>
    private sealed class ContainerVectorCache<T>(int length) where T : class, ISszCodec<T>
    {
        private readonly T?[] _snapshot = new T?[length];
        private readonly UInt256[] _roots = new UInt256[length];
        private bool _cached;

        public UInt256 Root(T[]? vector)
        {
            if (vector?.Length != length)
                throw new InvalidDataException($"Expected an SSZ vector of {length} {typeof(T).Name} elements, got {vector?.Length ?? 0}");

            for (int i = 0; i < length; i++)
            {
                T element = vector[i];
                if (_cached && ReferenceEquals(element, _snapshot[i]))
                    continue;
                T.Merkleize(element, out _roots[i]);
                _snapshot[i] = element;
            }
            _cached = true;

            Merkle.Merkleize(out UInt256 root, _roots);
            return root;
        }
    }

    /// <summary>The chunk tree a list cache writes its leaves into; follows the <see cref="MerkleChunkTree"/> contract.</summary>
    private interface IChunkTree
    {
        void SetLeafCount(int count);

        ref UInt256 Leaf(int index);

        void Rebuild();

        void Update(int[] dirtyIndices, int dirtyCount);

        /// <summary>The root of the chunks, before the list length is mixed in.</summary>
        UInt256 Root { get; }

        void Reset();
    }

    /// <summary>A bounded <c>List[T, N]</c> tree: one <see cref="MerkleChunkTree"/> as deep as the limit.</summary>
    private sealed class BoundedChunkTree(int depth) : IChunkTree
    {
        private readonly MerkleChunkTree _tree = new(depth);
        private UInt256[] _leaves = [];

        public void SetLeafCount(int count) => _leaves = _tree.SetLeafCount(count);
        public ref UInt256 Leaf(int index) => ref _leaves[index];
        public void Rebuild() => _tree.Rebuild();
        public void Update(int[] dirtyIndices, int dirtyCount) => _tree.Update(dirtyIndices, dirtyCount);
        public UInt256 Root => _tree.Root;

        public void Reset()
        {
            _tree.Reset();
            _leaves = [];
        }
    }

    /// <summary>
    /// An EIP-7916 <c>ProgressiveList</c> tree: subtrees of 1, 4, 16, ... chunks, each a
    /// <see cref="MerkleChunkTree"/>, chained as <c>hash(subtree_k, rest)</c> ending in a zero chunk.
    /// </summary>
    /// <remarks>Matches <see cref="Merkle.MerkleizeProgressive"/>, including the zero root of an empty tree.</remarks>
    private sealed class ProgressiveChunkTree : IChunkTree
    {
        private MerkleChunkTree[] _subtrees = [];
        private UInt256[][] _leaves = [];

        public void SetLeafCount(int count)
        {
            int subtreeCount = 0;
            while (SubtreeStart(subtreeCount) < count)
                subtreeCount++;

            int oldSubtreeCount = _subtrees.Length;
            if (subtreeCount != oldSubtreeCount)
            {
                Array.Resize(ref _subtrees, subtreeCount);
                Array.Resize(ref _leaves, subtreeCount);
                for (int k = oldSubtreeCount; k < subtreeCount; k++)
                {
                    _subtrees[k] = new MerkleChunkTree(2 * k);
                }
            }

            for (int k = 0; k < subtreeCount; k++)
            {
                _leaves[k] = _subtrees[k].SetLeafCount((int)Math.Min(count - SubtreeStart(k), 1L << (2 * k)));
            }
        }

        public ref UInt256 Leaf(int index)
        {
            int k = SubtreeOf(index);
            return ref _leaves[k][index - (int)SubtreeStart(k)];
        }

        public void Rebuild()
        {
            foreach (MerkleChunkTree subtree in _subtrees)
            {
                subtree.Rebuild();
            }
        }

        public void Update(int[] dirtyIndices, int dirtyCount)
        {
            int[] local = ArrayPool<int>.Shared.Rent(Math.Max(1, dirtyCount));
            int read = 0;
            while (read < dirtyCount)
            {
                int k = SubtreeOf(dirtyIndices[read]);
                long start = SubtreeStart(k);
                long end = SubtreeStart(k + 1);
                int localCount = 0;
                for (; read < dirtyCount && dirtyIndices[read] < end; read++)
                {
                    local[localCount++] = dirtyIndices[read] - (int)start;
                }
                _subtrees[k].Update(local, localCount);
            }
            ArrayPool<int>.Shared.Return(local);
        }

        public UInt256 Root
        {
            get
            {
                UInt256 root = UInt256.Zero;
                for (int k = _subtrees.Length - 1; k >= 0; k--)
                {
                    root = HashPair(_subtrees[k].Root, root);
                }
                return root;
            }
        }

        public void Reset()
        {
            _subtrees = [];
            _leaves = [];
        }

        /// <summary>The first leaf index of subtree <paramref name="k"/>, <c>(4^k - 1) / 3</c>.</summary>
        private static long SubtreeStart(int k) => ((1L << (2 * k)) - 1) / 3;
        private static int SubtreeOf(int index) => BitOperations.Log2(3UL * (uint)index + 1) >> 1;

        private static UInt256 HashPair(in UInt256 left, in UInt256 right)
        {
            Span<UInt256> pair = stackalloc UInt256[2];
            pair[0] = left;
            pair[1] = right;
            Span<byte> hash = stackalloc byte[32];
            ThreadSha256.HashData(MemoryMarshal.Cast<UInt256, byte>(pair), hash);
            return new UInt256(hash);
        }
    }
}
