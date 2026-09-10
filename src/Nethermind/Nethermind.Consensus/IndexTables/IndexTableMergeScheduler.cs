// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Consensus.IndexTables;

/// <summary>
/// Determines when higher-level EIP-8304 index tables should be published.
/// </summary>
/// <remarks>
/// A level-<c>i</c> table covering blocks <c>[firstBlock, firstBlock + TABLE_SIZES[i] - 1]</c>
/// is published at block <c>firstBlock + TABLE_SIZES[i] - 1 + TABLE_SIZES[i] / 4</c>.
/// The publication delay of <c>TABLE_SIZES[i] / 4</c> blocks allows time for
/// sub-table availability. Each higher-level table merges exactly 4 lower-level tables.
/// <para>See <see href="https://eips.ethereum.org/EIPS/eip-8304">EIP-8304</see>.</para>
/// </remarks>
public static class IndexTableMergeScheduler
{
    /// <summary>
    /// Returns the block number at which a level-<paramref name="level"/> table
    /// starting at <paramref name="firstBlock"/> should be published.
    /// </summary>
    /// <param name="level">The table level (1–4).</param>
    /// <param name="firstBlock">The first block covered by the table.</param>
    /// <returns>The block number at which the system call should be made.</returns>
    public static long PublicationBlock(int level, long firstBlock)
    {
        int tableSize = Eip8304Constants.TableSizes[level];
        int delay = tableSize / 4;
        return firstBlock + tableSize - 1 + delay;
    }

    /// <summary>
    /// For a given current block number, determines which higher-level tables
    /// (levels 1–4) should be published, and returns their parameters.
    /// </summary>
    /// <param name="blockNumber">The current block number being processed.</param>
    /// <param name="publishActions">
    /// Callback invoked for each table that should be published at this block.
    /// Parameters are (level, firstBlock, tableSize).
    /// </param>
    /// <param name="isForkActiveAt">
    /// Optional predicate to check whether the EIP-8304 fork was active at <c>firstBlock</c>.
    /// If provided and returns <c>false</c>, publication is skipped per the EIP-8304 specification.
    /// </param>
    public static void GetTablesForBlock(long blockNumber, PublishAction publishActions, Func<long, bool>? isForkActiveAt = null)
    {
        for (int level = 1; level < Eip8304Constants.TableSizes.Length; level++)
        {
            int tableSize = Eip8304Constants.TableSizes[level];
            int delay = tableSize / 4;

            // The table that would be published at `blockNumber` started at:
            // firstBlock + tableSize - 1 + delay = blockNumber
            // → firstBlock = blockNumber - tableSize + 1 - delay
            long candidateFirst = blockNumber - tableSize + 1 - delay;

            // firstBlock must be non-negative and aligned to tableSize
            if (candidateFirst < 0)
                continue;

            if (candidateFirst % tableSize != 0)
                continue;

            // EIP-8304: If the fork was not active at first_block, do not publish a table.
            if (isForkActiveAt is not null && !isForkActiveAt(candidateFirst))
                continue;

            publishActions(level, candidateFirst, tableSize);
        }
    }

    /// <summary>
    /// Delegate for table publication actions.
    /// </summary>
    /// <param name="level">The table level.</param>
    /// <param name="firstBlock">The first block covered by the table.</param>
    /// <param name="tableSize">The number of blocks in the table.</param>
    public delegate void PublishAction(int level, long firstBlock, int tableSize);
}
