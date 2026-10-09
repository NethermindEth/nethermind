// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.IndexTables;

/// <summary>
/// Persists sorted index entry lists keyed by (level, firstBlock) for multi-level
/// table merging and proof generation.
/// </summary>
/// <remarks>
/// An implementation may retain only the tables still needed for upcoming merges and recent proofs;
/// a missing table is rebuilt from the level below or from historical blocks and receipts.
/// <para>See <see href="https://eips.ethereum.org/EIPS/eip-8304">EIP-8304</see>.</para>
/// </remarks>
public interface IIndexTableStore
{
    /// <summary>
    /// Stores a sorted entry list for a given table level, first block number, and optional block hash.
    /// </summary>
    /// <param name="level">The table level (0–4).</param>
    /// <param name="firstBlock">The first block number covered by this table.</param>
    /// <param name="sortedEntries">The sorted index entries.</param>
    /// <param name="blockHash">The block hash identifying the branch or block. When omitted, default is used.</param>
    void Store(int level, long firstBlock, IReadOnlyList<IndexEntry> sortedEntries, Hash256? blockHash = null);

    /// <summary>
    /// Retrieves the sorted entry list for a given table level, first block number, and optional block hash.
    /// </summary>
    /// <param name="level">The table level (0–4).</param>
    /// <param name="firstBlock">The first block number covered by this table.</param>
    /// <param name="blockHash">The block hash identifying the branch. If null, the latest table for this block is returned.</param>
    /// <returns>The sorted entries, or <c>null</c> if not found.</returns>
    IReadOnlyList<IndexEntry>? Get(int level, long firstBlock, Hash256? blockHash = null);

    /// <summary>
    /// Removes a table entry for a specific block hash, or all branch variants if blockHash is null.
    /// </summary>
    void Remove(int level, long firstBlock, Hash256? blockHash = null);
}
