// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Tracing;

internal static class BlockReplayExtensions
{
    /// <summary>The block with copies of its transactions that keep their runtime type.</summary>
    /// <remarks>
    /// A replay writes into each transaction it runs the nonce it loads from state and the gas it spends. Under
    /// overridden state, block context or another fork's rules those can differ from the block's, so the replay runs
    /// on copies and the block tree's cached transactions keep their values.
    /// </remarks>
    public static Block WithOwnTransactions(this Block block) => block.WithReplacedBody(block.Body.WithOwnTransactions());

    /// <summary>The body with copies of its transactions that keep their runtime type, as <see cref="WithOwnTransactions(Block)"/>.</summary>
    public static BlockBody WithOwnTransactions(this BlockBody body)
    {
        Transaction[] transactions = body.Transactions;
        Transaction[] copies = new Transaction[transactions.Length];
        for (int i = 0; i < transactions.Length; i++)
        {
            copies[i] = transactions[i].ShallowCopy();
        }

        return body.WithChangedTransactions(copies);
    }
}
