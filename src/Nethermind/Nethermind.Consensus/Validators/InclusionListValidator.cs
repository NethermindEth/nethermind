// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Consensus.Validators;

public class InclusionListValidator(
    ISpecProvider specProvider,
    IWorldState worldState) : IInclusionListValidator
{
    public bool ValidateInclusionList(Block block, Func<Transaction, bool> isTransactionInBlock) =>
        ValidateInclusionList(block, isTransactionInBlock, specProvider.GetSpec(block.Header));

    private bool ValidateInclusionList(Block block, Func<Transaction, bool> isTransactionInBlock, IReleaseSpec spec)
    {
        if (!spec.InclusionListsEnabled)
        {
            return true;
        }

        if (block.InclusionListTransactions is null)
        {
            return false;
        }

        // There is no more gas for transactions so IL is satisfied
        // FOCIL is conditional IL
        if (block.GasUsed + Transaction.BaseTxGasCost > block.GasLimit)
        {
            return true;
        }

        foreach (Transaction tx in block.InclusionListTransactions)
        {
            if (isTransactionInBlock(tx))
            {
                continue;
            }

            if (block.GasUsed + tx.GasLimit > block.GasLimit)
            {
                continue;
            }

			// todo: check conditions
			UInt256 txCost = tx.Value + (UInt256)tx.GasLimit * tx.GasPrice;
            bool couldIncludeTx =
				tx.GasPrice >= block.BaseFeePerGas &&
				worldState.GetBalance(tx.SenderAddress) >= txCost &&
				worldState.GetNonce(tx.SenderAddress) == tx.Nonce;

            if (couldIncludeTx)
            {
                return false;
            }
        }

        return true;
    }
}
