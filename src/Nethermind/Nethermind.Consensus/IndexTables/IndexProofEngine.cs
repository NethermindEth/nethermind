// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.Consensus.IndexTables;

/// <summary>
/// Generates SSZ Merkle proofs for individual index entries against their table root.
/// </summary>
/// <remarks>
/// EIP-8304 proofs consist of:
/// <list type="number">
///   <item>The binary-encoded index entry itself.</item>
///   <item>An SSZ Merkle proof (sibling hashes along the path from the leaf to the root)
///         proving the entry's inclusion in the SSZ <c>List[Hash32, N]</c>.</item>
///   <item>The entry's leaf index in the sorted table.</item>
///   <item>The table parameters (level, firstBlock, tableSize) to locate the storage slot.</item>
/// </list>
/// <para>See <see href="https://eips.ethereum.org/EIPS/eip-8304">EIP-8304</see>.</para>
/// </remarks>
public static class IndexProofEngine
{
    /// <summary>
    /// Generates an SSZ Merkle proof for the entry at <paramref name="leafIndex"/>
    /// in a sorted entry list.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="leafIndex"/> is out of range.</exception>
    public static IndexEntryProof GenerateProof(IReadOnlyList<IndexEntry> sortedEntries, int leafIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(leafIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(leafIndex, sortedEntries.Count);

        return GenerateProofs(sortedEntries, [leafIndex])[0];
    }

    /// <summary>
    /// Generates proofs for several entries of the same table.
    /// </summary>
    /// <remarks>
    /// Callers needing more than one proof from a table must use this overload rather than calling
    /// <see cref="GenerateProof"/> repeatedly: the latter rehashes every entry per call, which is quadratic in the table size for queries matching many entries.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">If any leaf index is out of range.</exception>
    public static IndexEntryProof[] GenerateProofs(IReadOnlyList<IndexEntry> sortedEntries, IReadOnlyList<int> leafIndices)
    {
        if (leafIndices.Count == 0)
            return [];

        for (int i = 0; i < leafIndices.Count; i++)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(leafIndices[i], nameof(leafIndices));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(leafIndices[i], sortedEntries.Count, nameof(leafIndices));
        }

        int depth = Merkle.NextPowerOfTwoExponent((ulong)sortedEntries.Count);
        Dictionary<(int Level, int Position), UInt256> siblings = ComputeSiblings(sortedEntries, leafIndices, depth);
        IndexEntryProof[] proofs = new IndexEntryProof[leafIndices.Count];
        for (int i = 0; i < leafIndices.Count; i++)
        {
            proofs[i] = ExtractProof(sortedEntries, siblings, depth, leafIndices[i]);
        }

        return proofs;
    }

    /// <summary>
    /// Computes the sibling nodes on the paths from <paramref name="leafIndices"/> to the root,
    /// keyed by tree level and position within that level.
    /// </summary>
    /// <remarks>
    /// The leaf count is padded to <c>2^depth</c> with zero chunks, where <c>depth</c> matches the
    /// <c>List[Hash32, entry_count]</c> limit used by <see cref="IndexTableRootCalculator"/> — for a
    /// single-entry table that is depth 0, whose root is the leaf itself.
    /// <para>
    /// Leaves are folded left to right keeping one pending left node per level, so scratch memory is
    /// <c>O(depth)</c> plus the requested siblings instead of the whole padded tree; hashing stays <c>O(N)</c>.
    /// Subtrees made only of padding are not hashed: they are the precomputed zero hashes.
    /// </para>
    /// </remarks>
    private static Dictionary<(int Level, int Position), UInt256> ComputeSiblings(
        IReadOnlyList<IndexEntry> sortedEntries, IReadOnlyList<int> leafIndices, int depth)
    {
        Dictionary<(int Level, int Position), UInt256> siblings = new(leafIndices.Count * depth);
        for (int i = 0; i < leafIndices.Count; i++)
        {
            int position = leafIndices[i];
            for (int level = 0; level < depth; level++)
            {
                siblings.TryAdd((level, position ^ 1), Merkle.ZeroHashes[level]);
                position >>= 1;
            }
        }

        int count = sortedEntries.Count;
        UInt256[] pendingLeft = new UInt256[depth + 1];
        Span<byte> entryBuffer = stackalloc byte[IndexEntry.MaxEncodedLength];
        Span<byte> hashBuffer = stackalloc byte[SHA256.HashSizeInBytes];

        for (int i = 0; i < count; i++)
        {
            int encodedLength = sortedEntries[i].Encode(entryBuffer);
            SHA256.HashData(entryBuffer[..encodedLength], hashBuffer);
            UInt256 node = new(hashBuffer);

            int level = 0;
            int position = i;
            RecordSibling(siblings, level, position, node);
            while ((position & 1) == 1)
            {
                node = HashPair(pendingLeft[level], node);
                level++;
                position >>= 1;
                RecordSibling(siblings, level, position, node);
            }

            pendingLeft[level] = node;
        }

        // Close the partially filled right edge; nodes right of it hold only padding.
        if (count != 1 << depth)
        {
            UInt256 node = Merkle.ZeroHashes[0];
            for (int level = 0; level < depth; level++)
            {
                int position = count >> level;
                RecordSibling(siblings, level, position, node);
                node = (position & 1) == 1
                    ? HashPair(pendingLeft[level], node)
                    : HashPair(node, Merkle.ZeroHashes[level]);
            }
        }

        return siblings;
    }

    private static void RecordSibling(Dictionary<(int Level, int Position), UInt256> siblings, int level, int position, in UInt256 node)
    {
        ref UInt256 sibling = ref CollectionsMarshal.GetValueRefOrNullRef(siblings, (level, position));
        if (!Unsafe.IsNullRef(ref sibling))
            sibling = node;
    }

    private static IndexEntryProof ExtractProof(
        IReadOnlyList<IndexEntry> sortedEntries, Dictionary<(int Level, int Position), UInt256> siblings, int depth, int leafIndex)
    {
        UInt256[] proof = new UInt256[depth + 1];

        int position = leafIndex;
        for (int level = 0; level < depth; level++)
        {
            proof[level] = siblings[(level, position ^ 1)];
            position >>= 1;
        }

        // SSZ list roots mix in the length, so the last step of verification pairs the tree root
        // with the length chunk.
        proof[depth] = new UInt256((ulong)sortedEntries.Count, 0, 0, 0);

        byte[] entryBytes = new byte[sortedEntries[leafIndex].EncodedLength];
        sortedEntries[leafIndex].Encode(entryBytes);

        return new IndexEntryProof
        {
            Entry = entryBytes,
            LeafIndex = leafIndex,
            Proof = proof,
            ListLength = sortedEntries.Count,
        };
    }

    /// <summary>
    /// Computes the storage slot for a given table in the system contract.
    /// </summary>
    /// <remarks>
    /// EIP-8304: <c>slot = table_size × 1024 + (first_block / table_size) % 1024</c>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// If <paramref name="tableSize"/> is not positive or <paramref name="firstBlock"/> is negative,
    /// either of which would yield a slot outside the contract's ring buffer.
    /// </exception>
    public static UInt256 ComputeStorageSlot(int tableSize, long firstBlock)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tableSize);
        ArgumentOutOfRangeException.ThrowIfNegative(firstBlock);

        long slotIndex = (firstBlock / tableSize) % Eip8304Constants.TablesPerLevel;
        return new UInt256((ulong)((long)tableSize * Eip8304Constants.TablesPerLevel + slotIndex));
    }

    /// <summary>
    /// Hashes two 32-byte SSZ chunks (SHA-256 of their concatenation).
    /// </summary>
    internal static UInt256 HashPair(in UInt256 left, in UInt256 right)
    {
        Span<byte> buffer = stackalloc byte[64];
        left.ToLittleEndian(buffer[..32]);
        right.ToLittleEndian(buffer[32..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer, hash);
        return new UInt256(hash);
    }
}

/// <summary>
/// An SSZ Merkle proof for a single index entry against a table root.
/// </summary>
public class IndexEntryProof
{
    /// <summary>The binary-encoded index entry.</summary>
    public required byte[] Entry { get; init; }

    /// <summary>The leaf index of the entry in the sorted table.</summary>
    public required int LeafIndex { get; init; }

    /// <summary>
    /// The SSZ Merkle proof: sibling hashes from leaf to root, plus the length chunk.
    /// </summary>
    public required UInt256[] Proof { get; init; }

    /// <summary>The total number of entries in the table.</summary>
    public required int ListLength { get; init; }
}
