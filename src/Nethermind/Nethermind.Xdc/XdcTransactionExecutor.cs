// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Xdc.Spec;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

internal class XdcTransactionExecutor(ITransactionProcessorAdapter txProcessorAdapter, IWorldState stateProvider, ISpecProvider specProvider)
    : BlockProcessor.BlockValidationTransactionsExecutor(txProcessorAdapter, stateProvider)
{
    public override TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
    {
        Metrics.ResetBlockStats();

        var spec = specProvider.GetXdcSpec(block.Header as XdcBlockHeader);


        for (int i = 0; i < block.Transactions.Length; i++)
        {
            Transaction currentTx = block.Transactions[i];
            if (!currentTx.IsSpecialTransaction(spec))
                continue;

            ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);
        }

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            Transaction currentTx = block.Transactions[i];
            if (currentTx.IsSpecialTransaction(spec))
                continue;

            ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);
        }

        return receiptsTracer.TxReceipts.ToArray();
    }
}
