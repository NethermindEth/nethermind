// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Consensus.Validators;

/// <summary>
/// EIP-7805 (FOCIL) IL satisfaction check. A block satisfies the IL iff every IL tx is
/// either already included or no longer "appendable" — i.e., applying it as the next tx
/// against the post-execution state would fail (nonce gap, insufficient balance, base-fee
/// shortfall, or not enough remaining gas).
/// </summary>
/// <remarks>
/// State access is single-threaded: <see cref="IWorldState"/> backs onto a Patricia trie
/// with mutable caches, so reads MUST be serialised.
/// </remarks>
public class InclusionListValidator(
    ISpecProvider specProvider,
    IWorldState worldState) : IInclusionListValidator
{
    public bool ValidateInclusionList(Block block) =>
        ValidateInclusionList(block, specProvider.GetSpec(block.Header));

    private bool ValidateInclusionList(Block block, IReleaseSpec spec)
    {
        if (!spec.InclusionListsEnabled)
        {
            return true;
        }

        if (block.InclusionListTransactions is null)
        {
            return false;
        }

        // FOCIL is a conditional IL: once the block has no gas left for even a base-cost
        // transfer, by definition nothing more is appendable, so the IL is trivially satisfied.
        if (block.GasUsed + Transaction.BaseTxGasCost > block.GasLimit)
        {
            return true;
        }

        // Build the included-tx set once: O(n) construction trades a hash for the O(n*m)
        // worst case of scanning block.Transactions per IL tx. ByHashTxComparer matches what
        // the executor uses elsewhere so cross-instance hash collisions can't false-match.
        HashSet<Transaction> includedTxs = new(block.Transactions, ByHashTxComparer.Instance);

        // Serial scan — worldState is not thread-safe (see <remarks> on the class).
        foreach (Transaction tx in block.InclusionListTransactions)
        {
            if (!includedTxs.Contains(tx) && CouldIncludeTx(tx, block))
            {
                return false;
            }
        }

        return true;
    }

    private bool CouldIncludeTx(Transaction tx, Block block)
    {
        // A tx whose signature didn't recover has no sender we can score against the
        // worldstate. Treat it as not-appendable so we don't NRE on GetBalance(null) and
        // don't falsely report the IL as unsatisfied for an unsignable entry.
        if (tx.SenderAddress is null)
        {
            return false;
        }

        if (block.GasUsed + tx.GasLimit > block.GasLimit)
        {
            return false;
        }

        UInt256 txCost = tx.Value + (UInt256)tx.GasLimit * tx.GasPrice;
        return tx.GasPrice >= block.BaseFeePerGas &&
            worldState.GetBalance(tx.SenderAddress) >= txCost &&
            worldState.GetNonce(tx.SenderAddress) == tx.Nonce;
    }
}
