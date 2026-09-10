// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.Consensus.IndexTables;

/// <summary>
/// Computes the SSZ table root for a set of EIP-8304 index entries.
/// </summary>
/// <remarks>
/// EIP-8304 table roots are SSZ <c>List[Hash32, entry_count]</c> roots:
/// <list type="number">
///   <item>Each entry is binary-encoded and individually SHA-256 hashed.</item>
///   <item>The resulting 32-byte hashes are treated as SSZ chunks.</item>
///   <item>The chunks are merkleized with a limit equal to the total entry count.</item>
///   <item>The entry count is mixed in as the SSZ list length.</item>
/// </list>
/// <para>See <see href="https://eips.ethereum.org/EIPS/eip-8304">EIP-8304</see>.</para>
/// </remarks>
public static class IndexTableRootCalculator
{
    /// <summary>
    /// Computes the SSZ table root from a list of index entries.
    /// </summary>
    /// <remarks>
    /// Entries are sorted, SHA-256 hashed individually, then merkleized as
    /// <c>List[Hash32, entry_count]</c> with length mix-in per SSZ list semantics.
    /// The input list is sorted in-place.
    /// </remarks>
    /// <param name="entries">The entries to include in the table. Will be sorted in-place.</param>
    /// <returns>The 32-byte SSZ table root.</returns>
    public static UInt256 ComputeRoot(List<IndexEntry> entries)
    {
        if (entries.Count == 0)
        {
            // Empty list: merkleize zero chunks with length mix-in of 0
            UInt256 emptyRoot = UInt256.Zero;
            Merkle.MixIn(ref emptyRoot, 0);
            return emptyRoot;
        }

        // Sort entries lexicographically by binary encoding
        entries.Sort();

        // SHA-256 hash each entry to produce 32-byte leaf chunks
        UInt256[] leafHashes = new UInt256[entries.Count];
        Span<byte> entryBuffer = stackalloc byte[IndexEntry.MaxEncodedLength];
        Span<byte> hashBuffer = stackalloc byte[SHA256.HashSizeInBytes];

        for (int i = 0; i < entries.Count; i++)
        {
            int encodedLength = entries[i].Encode(entryBuffer);
            SHA256.HashData(entryBuffer[..encodedLength], hashBuffer);
            leafHashes[i] = new UInt256(hashBuffer);
        }

        // Merkleize as List[Hash32, entry_count]: limit = entry count
        Merkle.Merkleize(out UInt256 root, leafHashes.AsSpan(), (ulong)entries.Count);

        // Mix in the length (SSZ list semantics)
        Merkle.MixIn(ref root, entries.Count);

        return root;
    }
}
