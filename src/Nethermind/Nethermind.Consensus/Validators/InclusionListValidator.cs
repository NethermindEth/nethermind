// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Validators;

public class InclusionListValidator(
    ISpecProvider specProvider,
    ITransactionProcessor transactionProcessor) : IInclusionListValidator
{
    private readonly ISpecProvider _specProvider = specProvider;
    private readonly ITransactionProcessor _transactionProcessor = transactionProcessor;

    public bool ValidateInclusionList(Block block, Func<Transaction, bool> isTransactionInBlock) =>
        ValidateInclusionList(block, isTransactionInBlock, _specProvider.GetSpec(block.Header));

    public bool ValidateInclusionList(Block block, Func<Transaction, bool> isTransactionInBlock, IReleaseSpec spec)
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

            bool couldIncludeTx = _transactionProcessor.CallAndRestore(tx, new(block.Header, spec), NullTxTracer.Instance);
            if (couldIncludeTx)
            {
                return false;
            }
        }

        return true;
    }
}
