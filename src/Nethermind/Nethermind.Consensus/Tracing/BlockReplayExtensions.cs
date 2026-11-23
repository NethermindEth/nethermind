// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;

[assembly: InternalsVisibleTo("Nethermind.LightClient")]

namespace Nethermind.Consensus.Tracing;

internal static class BlockReplayExtensions
{
    /// <summary>The block with copies of its transactions, limited to <paramref name="transactionCount"/> when supplied.</summary>
    /// <remarks>
    /// A replay writes into each transaction it runs the nonce it loads from state, the sender it recovers and the gas
    /// it spends. A replay therefore runs on copies, so that the block tree's transactions keep their values.
    /// </remarks>
    public static Block WithOwnTransactions(this Block block, int? transactionCount = null)
    {
        Transaction[] transactions = block.Transactions;
        Transaction[] copies = new Transaction[transactionCount ?? transactions.Length];
        for (int i = 0; i < copies.Length; i++)
        {
            copies[i] = transactions[i].ShallowCopy();
        }

        return block.WithReplacedBody(block.Body.WithChangedTransactions(copies));
    }
}
