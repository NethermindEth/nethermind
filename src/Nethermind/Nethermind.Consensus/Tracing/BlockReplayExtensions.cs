// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Tracing;

internal static class BlockReplayExtensions
{
    /// <summary>The block with copies of its transactions that keep their runtime type.</summary>
    /// <remarks>
    /// A replay writes into each transaction it runs the nonce it loads from state, the sender it recovers and the gas
    /// it spends. A replay therefore runs on copies, so that the block tree's transactions keep their values.
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
