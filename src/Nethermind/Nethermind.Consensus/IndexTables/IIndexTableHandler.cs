// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.IndexTables;

/// <summary>
/// Handles EIP-8304 index table root commitments during block processing.
/// </summary>
public interface IIndexTableHandler
{
    /// <summary>
    /// Computes and stores the index table root(s) for the given block via system contract call(s).
    /// </summary>
    /// <param name="block">The block being processed.</param>
    /// <param name="receipts">The block's transaction receipts.</param>
    /// <param name="spec">The active release spec.</param>
    /// <param name="tracer">The transaction tracer.</param>
    void CommitIndexTableRoots(Block block, TxReceipt[] receipts, IReleaseSpec spec, ITxTracer tracer) { }

    /// <summary>
    /// Rolls back any cached index tables published by a failed or rejected block.
    /// </summary>
    /// <param name="block">The block to roll back.</param>
    void RollbackBlock(Block block) { }

    /// <summary>
    /// Updates cached index tables with the block's finalized header hash after post-execution state root calculation.
    /// </summary>
    /// <param name="block">The finalized block.</param>
    void UpdateFinalBlockHash(Block block) { }
}
