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
            if (!IsSpecial(spec, currentTx))
                continue;
            ProcessSpecialTransaction(block, currentTx, i, receiptsTracer, processingOptions, spec);
        }

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            Transaction currentTx = block.Transactions[i];
            if (IsSpecial(spec, currentTx))
                continue;

            ProcessNormalTransaction(block, currentTx, i, receiptsTracer, processingOptions, spec);
        }

        return receiptsTracer.TxReceipts.ToArray();
    }

    private void ProcessNormalTransaction(Block block, Transaction currentTx, int i, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions, IXdcReleaseSpec spec)
    {
        Address sender = currentTx.SenderAddress;
        Address target = currentTx.To;
        if (IsBlackListed(spec, sender) || IsBlackListed(spec, target))
        {
            // Skip processing special transactions if either sender or recipient is blacklisted
            return;
        }

        ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);
    }

    private void ProcessSpecialTransaction(Block block, Transaction currentTx, int i, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions, IXdcReleaseSpec spec)
    {
        Address sender = currentTx.SenderAddress;
        Address target = currentTx.To;
        if (spec.BlackListHFNumber == block.Number)
        {
            if (IsBlackListed(spec, sender))
            {
                // Skip processing special transactions if either sender or recipient is blacklisted
                return;
            }
        }

        if(target == spec.BlockSignersAddress)
        {
            if(currentTx.Data.Length < 68) {
                return;
            }

            UInt256 blkNumber = new UInt256(currentTx.Data.Span[8..40], true);
            if (blkNumber >= (UInt256)block.Number || blkNumber <= (UInt256)(block.Number - (spec.EpochLength * 2)))
            {
                // Invalid block number in special transaction data
                return;
            }

        }

        ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);
    }

    private bool IsBlackListed(IXdcReleaseSpec spec, Address sender)
    {
        throw new NotImplementedException();
    }

    private bool IsSpecial(IXdcReleaseSpec spec, Transaction currentTx)
    {
        return currentTx.To is not null && ((currentTx.To == spec.BlockSignersAddress) || (currentTx.To == spec.RandomizeSMCBinary));
    }
}
