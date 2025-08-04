// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

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

        bool couldIncludeTx = block.InclusionListTransactions
            .AsParallel()
            .Any(tx => !isTransactionInBlock(tx) && CouldIncludeTx(tx, block));

        return !couldIncludeTx;
    }

    private bool CouldIncludeTx(Transaction tx, Block block)
    {
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
