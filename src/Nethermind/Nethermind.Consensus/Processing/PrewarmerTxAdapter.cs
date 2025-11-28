// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Processing;

public class PrewarmerTxAdapter(ITransactionProcessorAdapter baseAdapter, BlockCachePreWarmer preWarmer) : ITransactionProcessorAdapter
{
    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        preWarmer.OnBeforeTxExecution(transaction);
        return baseAdapter.Execute(transaction, txTracer);
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) => baseAdapter.SetBlockExecutionContext(in blockExecutionContext);

}
