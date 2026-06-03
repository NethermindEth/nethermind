// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Consensus.Validators;

public static class InclusionListValidator
{
    public static bool IsSatisfied(Block block, IAccountStateProvider state, IReleaseSpec spec)
    {
        if (!spec.InclusionListsEnabled) return true;
        if (block.InclusionListTransactions is null) return false;

        // FOCIL is conditional: no gas left for a base-cost transfer → nothing is appendable.
        if (block.GasUsed + Transaction.BaseTxGasCost > block.GasLimit) return true;

        HashSet<Transaction> includedTxs = new(block.Transactions, ByHashTxComparer.Instance);
        foreach (Transaction tx in block.InclusionListTransactions)
        {
            if (!includedTxs.Contains(tx) && CouldIncludeTx(tx, block, state)) return false;
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
