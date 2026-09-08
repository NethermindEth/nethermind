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
/// Each level maintains a ring buffer of <see cref="Nethermind.Core.Eip8304Constants.TablesPerLevel"/>
/// tables. When a new table is stored and the ring buffer is full, the oldest table at
/// that level is evicted. Implementations must survive node restarts.
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
    /// Removes a table entry, used during reorg invalidation.
    /// </summary>
    void Remove(int level, long firstBlock);

    /// <summary>
    /// Removes every table that covers a block above <paramref name="blockNumber"/>.
    /// Used after a chain reorganization to discard invalidated tables.
    /// </summary>
    /// <remarks>
    /// A level-<c>i</c> table spans <c>[firstBlock, firstBlock + TABLE_SIZES[i] - 1]</c>, so a table
    /// whose first block is at or below <paramref name="blockNumber"/> may still contain
    /// reorged-out blocks and must be dropped.
    /// </remarks>
    /// <param name="blockNumber">The highest block number that remains valid.</param>
    void InvalidateAbove(long blockNumber);
}
