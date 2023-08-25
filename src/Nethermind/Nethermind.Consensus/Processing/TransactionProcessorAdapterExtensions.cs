// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing
{
    internal static class TransactionProcessorAdapterExtensions
    {
        public static void ProcessTransaction(this ITransactionProcessorAdapter transactionProcessor,
            Block block,
            Transaction currentTx,
            BlockExecutionTracer receiptsTracer,
            ProcessingOptions processingOptions,
            IWorldState stateProvider)
        {
            if (processingOptions.ContainsFlag(ProcessingOptions.DoNotVerifyNonce))
            {
                currentTx.Nonce = stateProvider.GetNonce(currentTx.SenderAddress);
            }

            receiptsTracer.StartNewTxTrace(currentTx);
            transactionProcessor.Execute(currentTx, block.Header, receiptsTracer);
            receiptsTracer.EndTxTrace();
        }
    }
}
