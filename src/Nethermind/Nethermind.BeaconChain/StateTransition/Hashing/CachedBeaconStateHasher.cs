// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
/// No mutation hooks: each call compares the state against the snapshot taken on the previous
/// call. The result is correct for any state whose unchanged content is reference-identical
/// (validators, sync committees, the payload header, and the root vectors — all
/// immutable-by-convention objects) or value-identical (balances, participation, inactivity
/// scores) to the snapshot. This holds both for in-place mutation of the same state instance and
/// for states produced by <see cref="BeaconStateClone.Clone"/>, since clones share element
/// references with their source.
/// </para>
/// <para>
/// Cached per field: validators (per-validator leaf roots plus the tree above, diffed by
/// <see cref="object.ReferenceEquals"/>), balances and inactivity scores (4-ulong chunk diff),
/// both participation lists (32-byte chunk diff), the block/state-root and RANDAO-mix vectors
/// (per-element reference diff), and the sync committees and payload header (whole-object
/// reference memos). The remaining ~26 small fields are re-merkleized on every call. When more
/// than half of a tree's chunks are dirty (e.g. the epoch-boundary balance rewrite), the subtree
/// is rebuilt level-by-level in parallel instead of patching paths.
/// </para>
/// <para>
/// Use one instance per followed state lineage, like the rest of <see cref="EpochCache"/>. After
/// a reorg either keep the instance (hashing a sibling state stays correct, only the first call
/// pays for the larger diff) or call <see cref="Reset"/> to drop the caches. Not thread-safe.
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
    private const int RootsVectorDepth = 13;
    private const int RandaoMixesDepth = 16;
    private const ulong SlashingsChunkCount = 2048;
    private const ulong ProposerLookaheadChunkCount = 16;
    private const int FieldCount = 38;
    private const int ParallelLeafThreshold = 2048;

    private static readonly Fork s_defaultFork = new();
    private static readonly BeaconBlockHeader s_defaultLatestBlockHeader = new();
    private static readonly Eth1Data s_defaultEth1Data = new();
    private static readonly Checkpoint s_defaultCheckpoint = new();
    private static readonly BitArray s_defaultJustificationBits = new(4);

    private readonly ValidatorListCache _validators = new();
    private readonly BasicListCache _balances = new(UInt64ListDepth);
    private readonly BasicListCache _inactivityScores = new(UInt64ListDepth);
    private readonly BasicListCache _previousEpochParticipation = new(ParticipationDepth);
    private readonly BasicListCache _currentEpochParticipation = new(ParticipationDepth);
    private readonly HashVectorCache _blockRoots = new(RootsVectorDepth);
    private readonly HashVectorCache _stateRoots = new(RootsVectorDepth);
    private readonly HashVectorCache _randaoMixes = new(RandaoMixesDepth);
    private readonly ContainerMemo<SyncCommittee> _currentSyncCommittee = new();
    private readonly ContainerMemo<SyncCommittee> _nextSyncCommittee = new();
    private readonly ContainerMemo<ExecutionPayloadHeader> _latestExecutionPayloadHeader = new();
    private readonly UInt256[] _fieldRoots = new UInt256[FieldCount];

    /// <inheritdoc/>
    public Hash256 HashTreeRoot(BeaconStateFulu state)
    {
        // The generated Merkleize rejects this before computing anything; keep the same contract.
        if (state.LatestExecutionPayloadHeader is null)
            throw new InvalidDataException($"Invalid SSZ value for {nameof(BeaconStateFulu)}.{nameof(state.LatestExecutionPayloadHeader)}: null variable fields are not decodable.");

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
        roots[7] = RootListRoot(state.HistoricalRoots, HistoricalRootsLimit);
        Eth1Data.Merkleize(state.Eth1Data ?? s_defaultEth1Data, out roots[8]);
        Eth1Data.MerkleizeList(state.Eth1DataVotes ?? [], 2048, out roots[9]);
        roots[10] = new UInt256(state.Eth1DepositIndex);
        roots[11] = _validators.Root(state.Validators);
        roots[12] = _balances.Root(MemoryMarshal.AsBytes<ulong>(state.Balances), state.Balances?.Length ?? 0);
        roots[13] = _randaoMixes.Root(state.RandaoMixes);
        Merkle.Merkleize(out roots[14], MemoryMarshal.AsBytes<ulong>(state.Slashings), SlashingsChunkCount);
        roots[15] = _previousEpochParticipation.Root(state.PreviousEpochParticipation, state.PreviousEpochParticipation?.Length ?? 0);
        roots[16] = _currentEpochParticipation.Root(state.CurrentEpochParticipation, state.CurrentEpochParticipation?.Length ?? 0);
        Merkle.Merkleize(out roots[17], state.JustificationBits ?? s_defaultJustificationBits);
        Checkpoint.Merkleize(state.PreviousJustifiedCheckpoint ?? s_defaultCheckpoint, out roots[18]);
        Checkpoint.Merkleize(state.CurrentJustifiedCheckpoint ?? s_defaultCheckpoint, out roots[19]);
        Checkpoint.Merkleize(state.FinalizedCheckpoint ?? s_defaultCheckpoint, out roots[20]);
        roots[21] = _inactivityScores.Root(MemoryMarshal.AsBytes<ulong>(state.InactivityScores), state.InactivityScores?.Length ?? 0);
        roots[22] = _currentSyncCommittee.Root(state.CurrentSyncCommittee);
        roots[23] = _nextSyncCommittee.Root(state.NextSyncCommittee);
        roots[24] = _latestExecutionPayloadHeader.Root(state.LatestExecutionPayloadHeader);
        roots[25] = new UInt256(state.NextWithdrawalIndex);
        roots[26] = new UInt256(state.NextWithdrawalValidatorIndex);
        HistoricalSummary.MerkleizeList(state.HistoricalSummaries ?? [], 16_777_216, out roots[27]);
        roots[28] = new UInt256(state.DepositRequestsStartIndex);
        roots[29] = new UInt256(state.DepositBalanceToConsume);
        roots[30] = new UInt256(state.ExitBalanceToConsume);
        roots[31] = new UInt256(state.EarliestExitEpoch);
        roots[32] = new UInt256(state.ConsolidationBalanceToConsume);
        roots[33] = new UInt256(state.EarliestConsolidationEpoch);
        PendingDeposit.MerkleizeList(state.PendingDeposits ?? [], 134_217_728, out roots[34]);
        PendingPartialWithdrawal.MerkleizeList(state.PendingPartialWithdrawals ?? [], 134_217_728, out roots[35]);
        PendingConsolidation.MerkleizeList(state.PendingConsolidations ?? [], 262_144, out roots[36]);
        Merkle.Merkleize(out roots[37], MemoryMarshal.AsBytes<ulong>(state.ProposerLookahead), ProposerLookaheadChunkCount);

        Merkle.Merkleize(out UInt256 root, roots);
        return new Hash256(root.ToLittleEndian());
    }

    /// <summary>Drops every snapshot and cached subtree; the next call hashes from scratch.</summary>
    public void Reset()
    {
        _validators.Reset();
        _balances.Reset();
        _inactivityScores.Reset();
        _previousEpochParticipation.Reset();
        _currentEpochParticipation.Reset();
        _blockRoots.Reset();
        _stateRoots.Reset();
        _randaoMixes.Reset();
        _currentSyncCommittee.Reset();
        _nextSyncCommittee.Reset();
        _latestExecutionPayloadHeader.Reset();
    }

    /// <summary>Computes the root of a <c>List[Root, limit]</c> (small on mainnet: frozen since Capella).</summary>
    private static UInt256 RootListRoot(Hash256[]? hashes, ulong limit)
    {
        int count = hashes?.Length ?? 0;
        UInt256[] chunks = ArrayPool<UInt256>.Shared.Rent(count);
        for (int i = 0; i < count; i++)
        {
            chunks[i] = new UInt256(hashes![i].Bytes);
        }
        Merkle.Merkleize(out UInt256 root, chunks.AsSpan(0, count), limit);
        ArrayPool<UInt256>.Shared.Return(chunks);
        Merkle.MixIn(ref root, count);
        return root;
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

        public void Reset()
        {
            _last = null;
            _cached = false;
        }
    }

    /// <summary>
    /// Caches <c>List[Validator, 2^40]</c>: per-validator leaf roots plus the tree above, diffed
    /// by element reference against the previous call (validators are immutable-by-convention, so
    /// a mutation is always a replaced instance).
    /// </summary>
    private sealed class ValidatorListCache
    {
        private readonly MerkleChunkTree _tree = new(ValidatorsDepth);
        private Validator?[] _snapshot = [];
        private int _count;

        public UInt256 Root(Validator[]? validators)
        {
            int count = validators?.Length ?? 0;
            bool shrunk = count < _count;
            if (shrunk)
                _count = 0;
            UInt256[] leaves = _tree.SetLeafCount(count);
            if (_snapshot.Length < count)
                Array.Resize(ref _snapshot, Math.Max(count, _snapshot.Length * 2));

            int[] dirty = ArrayPool<int>.Shared.Rent(Math.Max(1, count));
            int dirtyCount = 0;
            for (int i = 0; i < count; i++)
            {
                Validator validator = validators![i];
                if (i < _count && ReferenceEquals(validator, _snapshot[i]))
                    continue;
                _snapshot[i] = validator;
                dirty[dirtyCount++] = i;
            }
            _count = count;

            if (dirtyCount >= ParallelLeafThreshold)
            {
                int[] dirtyLocal = dirty;
                Validator[] validatorsLocal = validators!;
                Parallel.For(0, dirtyCount, r =>
                {
                    int i = dirtyLocal[r];
                    Validator.Merkleize(validatorsLocal[i], out leaves[i]);
                });
            }
            else
                for (int r = 0; r < dirtyCount; r++)
                {
                    int i = dirty[r];
                    Validator.Merkleize(validators![i], out leaves[i]);
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

        public void Reset()
        {
            _tree.Reset();
            _snapshot = [];
            _count = 0;
        }
    }

    /// <summary>
    /// Caches a basic-type SSZ list (uint64 balances/inactivity scores, byte participation):
    /// a value snapshot diffed per 32-byte chunk, with dirty-path updates.
    /// </summary>
    private sealed class BasicListCache(int depth)
    {
        private readonly MerkleChunkTree _tree = new(depth);
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

            UInt256[] leaves = _tree.SetLeafCount(chunkCount);
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
                leaves[i] = dataChunks[i];
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
                    leaves[fullChunks] = chunk;
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
                _snapshot[i] = element;
                leaves[i] = element is null ? default : new UInt256(element.Bytes);
                dirty[dirtyCount++] = i;
            }

            if (initial || dirtyCount * 2 > vector.Length)
                _tree.Rebuild();
            else if (dirtyCount > 0)
                _tree.Update(dirty, dirtyCount);
            ArrayPool<int>.Shared.Return(dirty);

            return _tree.Root;
        }

        public void Reset()
        {
            _tree.Reset();
            _snapshot = [];
        }
    }
}
