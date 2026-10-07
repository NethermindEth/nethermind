// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Tracing;

public static class BlockReplayExtensions
{
    /// <summary>The block with copies of its transactions that keep their runtime type.</summary>
    /// <remarks>
    /// A replay writes the nonce it loads from state into each transaction. Under overridden state or another
    /// fork's rules that nonce can differ, and the transactions of a block from the block tree are the instances
    /// the rest of the node reads.
    /// </remarks>
    public static Block WithOwnTransactions(this Block block)
    {
        Transaction[] transactions = block.Transactions;
        Transaction[] copies = new Transaction[transactions.Length];
        for (int i = 0; i < transactions.Length; i++)
        {
            copies[i] = transactions[i].ShallowCopy();
        }

        return block.WithReplacedBody(block.Body.WithChangedTransactions(copies));
    }
}
