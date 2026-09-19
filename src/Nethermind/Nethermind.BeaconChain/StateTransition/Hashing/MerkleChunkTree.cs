// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.BeaconChain.StateTransition.Hashing;

/// <summary>
/// An incrementally updatable binary Merkle tree over 32-byte chunks, virtualized to a fixed
/// <paramref name="depth"/> with the SSZ zero-subtree hashes.
/// </summary>
/// <remarks>
/// Matches the padding semantics of <see cref="Merkle.Merkleize(out UInt256, ReadOnlySpan{UInt256}, ulong)"/>
/// with a limit of <c>2^depth</c> chunks: missing right siblings hash against
/// <see cref="Merkle.ZeroHashes"/> and the root is chained with zero hashes up to
/// <paramref name="depth"/>. Only the levels needed for the current leaf count are materialized,
/// so memory is ~2x the leaf chunks regardless of the virtual depth. Not thread-safe.
/// </remarks>
/// <param name="depth">The virtual tree depth; the chunk limit is <c>2^depth</c>.</param>
internal sealed class MerkleChunkTree(int depth)
{
    private const int ParallelThreshold = 8192;

    private UInt256[][] _levels = [];
    private int _leafCount;

    /// <summary>
    /// Resizes the leaf level to <paramref name="count"/> chunks and returns the leaf array
    /// (longer than <paramref name="count"/> entries is never returned).
    /// </summary>
    /// <remarks>
    /// Existing leaves and interior nodes are preserved; new slots are zero. After growing, every
    /// appended leaf must be reported dirty to <see cref="Update"/> (or <see cref="Rebuild"/>
    /// called) so the new interior nodes are computed; after shrinking, <see cref="Rebuild"/> must
    /// be called because boundary interior nodes become stale.
    /// </remarks>
    public UInt256[] SetLeafCount(int count)
    {
        if (count != _leafCount)
        {
            int levelCount = count == 0 ? 0 : Merkle.NextPowerOfTwoExponent((ulong)count) + 1;
            UInt256[][] oldLevels = _levels;
            if (oldLevels.Length != levelCount)
                _levels = new UInt256[levelCount][];
            for (int level = 0; level < levelCount; level++)
            {
                int length = LevelLength(count, level);
                UInt256[] nodes = level < oldLevels.Length ? oldLevels[level] : [];
                if (nodes.Length != length)
                    Array.Resize(ref nodes, length);
                _levels[level] = nodes;
            }
            _leafCount = count;
        }
        return _levels.Length == 0 ? [] : _levels[0];
    }

    /// <summary>Recomputes every interior node from the current leaves.</summary>
    public void Rebuild()
    {
        for (int level = 0; level + 1 < _levels.Length; level++)
        {
            UInt256[] children = _levels[level];
            UInt256[] parents = _levels[level + 1];
            int childCount = LevelLength(_leafCount, level);
            int parentCount = LevelLength(_leafCount, level + 1);
            int childLevel = level;
            if (parentCount >= ParallelThreshold)
                Parallel.For(0, parentCount, parent => parents[parent] = ComputeParent(children, childCount, parent, childLevel));
            else
                for (int parent = 0; parent < parentCount; parent++)
                {
                    parents[parent] = ComputeParent(children, childCount, parent, childLevel);
                }
        }
    }

    /// <summary>Recomputes the paths above the given leaves after their chunks were rewritten.</summary>
    /// <param name="dirtyIndices">Sorted, distinct leaf indices; the buffer is clobbered as scratch space.</param>
    /// <param name="dirtyCount">The number of valid entries in <paramref name="dirtyIndices"/>.</param>
    public void Update(int[] dirtyIndices, int dirtyCount)
    {
        for (int level = 0; level + 1 < _levels.Length && dirtyCount > 0; level++)
        {
            UInt256[] children = _levels[level];
            UInt256[] parents = _levels[level + 1];
            int childCount = LevelLength(_leafCount, level);
            int childLevel = level;

            // Map the dirty child indices to their distinct parent indices in place.
            int write = 0;
            for (int read = 0; read < dirtyCount; read++)
            {
                int parent = dirtyIndices[read] >> 1;
                if (write > 0 && dirtyIndices[write - 1] == parent)
                    continue;
                dirtyIndices[write++] = parent;
            }
            dirtyCount = write;

            if (dirtyCount >= ParallelThreshold)
                Parallel.For(0, dirtyCount, i =>
                {
                    int parent = dirtyIndices[i];
                    parents[parent] = ComputeParent(children, childCount, parent, childLevel);
                });
            else
                for (int i = 0; i < dirtyCount; i++)
                {
                    int parent = dirtyIndices[i];
                    parents[parent] = ComputeParent(children, childCount, parent, childLevel);
                }
        }
    }

    /// <summary>The tree root at the virtual <c>depth</c>, including the zero-subtree chaining.</summary>
    public UInt256 Root
    {
        get
        {
            if (_leafCount == 0)
                return Merkle.ZeroHashes[depth];

            UInt256 root = _levels[^1][0];
            for (int level = _levels.Length - 1; level < depth; level++)
            {
                root = HashPair(root, Merkle.ZeroHashes[level], level);
            }
            return root;
        }
    }

    /// <summary>Drops all nodes; the next <see cref="SetLeafCount"/> starts from an empty tree.</summary>
    public void Reset()
    {
        _levels = [];
        _leafCount = 0;
    }

    private static int LevelLength(int leafCount, int level) => (int)(((long)leafCount + (1L << level) - 1) >> level);

    private static UInt256 ComputeParent(UInt256[] children, int childCount, int parentIndex, int level)
    {
        int left = parentIndex << 1;
        int right = left | 1;
        return HashPair(children[left], right < childCount ? children[right] : Merkle.ZeroHashes[level], level);
    }

    private static UInt256 HashPair(in UInt256 left, in UInt256 right, int level)
    {
        // Same shortcut as Merkle.HashConcatenation: an all-zero subtree pair hashes to the
        // precomputed zero hash of the level above.
        if (left == Merkle.ZeroHashes[level] && right == Merkle.ZeroHashes[level])
            return Merkle.ZeroHashes[level + 1];

        Span<UInt256> pair = stackalloc UInt256[2];
        pair[0] = left;
        pair[1] = right;
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(MemoryMarshal.Cast<UInt256, byte>(pair), hash);
        return new UInt256(hash);
    }
}
