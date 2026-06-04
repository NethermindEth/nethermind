// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Consensus.Validators;

public static class InclusionListValidator
{
    public static bool IsSatisfied(Block block, IAccountStateProvider state, IReleaseSpec spec)
    {
        if (!spec.InclusionListsEnabled) return true;
        Transaction[]? il = block.InclusionListTransactions;
        if (il is null) return false;

        // FOCIL is conditional: no gas left for a base-cost transfer → nothing is appendable.
        if (block.GasUsed + Transaction.BaseTxGasCost > block.GasLimit) return true;

        // Mark IL entries already present in the block via linear scan. Stack-allocate the bitmap
        // when IL fits within the spec cap; pool the heap fallback for malformed oversize inputs.
        using ArrayPoolList<bool>? pooled = il.Length > Eip7805Constants.MaxTransactionsPerInclusionList
            ? new ArrayPoolList<bool>(il.Length, il.Length)
            : null;
        Span<bool> included = pooled is null ? stackalloc bool[il.Length] : pooled.AsSpan();

        foreach (Transaction blockTx in block.Transactions)
        {
            for (int i = 0; i < il.Length; i++)
            {
                if (!included[i] && blockTx.Hash == il[i].Hash)
                {
                    included[i] = true;
                    break;
                }
            }
        }

        for (int i = 0; i < il.Length; i++)
        {
            if (!included[i] && CouldIncludeTx(il[i], block, state)) return false;
        }
        return true;
    }

    private static bool CouldIncludeTx(Transaction tx, Block block, IAccountStateProvider state)
    {
        if (tx.SenderAddress is null) return false;
        if (block.GasUsed + tx.GasLimit > block.GasLimit) return false;

        // EIP-1559: compare baseFee against the cap (MaxFeePerGas), not the priority tip
        // (which is what tx.GasPrice exposes for type-2). Matches TransactionProcessor.
        if (tx.MaxFeePerGas < block.BaseFeePerGas) return false;

        if (!state.TryGetAccount(tx.SenderAddress, out AccountStruct account)) return false;
        UInt256 txCost = tx.Value + (UInt256)tx.GasLimit * tx.MaxFeePerGas;
        return account.Balance >= txCost && account.Nonce == tx.Nonce;
    }
}
