// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public class ParallelBlockValidationTransactionsExecutor() : IBlockProcessor.IBlockTransactionsExecutor
{
    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec)
    {
        throw new NotImplementedException();
    }

    public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;
}
