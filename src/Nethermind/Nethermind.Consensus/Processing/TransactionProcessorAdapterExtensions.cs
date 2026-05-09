// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Processing;

internal static class TransactionProcessorAdapterExtensions
{
    public static TransactionResult ProcessTransaction(this ITransactionProcessorAdapter transactionProcessor,
        Transaction currentTx,
        BlockReceiptsTracer receiptsTracer,
        ProcessingOptions processingOptions,
        IWorldState stateProvider)
    {
        if (processingOptions.ContainsFlag(ProcessingOptions.LoadNonceFromState) && currentTx.SenderAddress != Address.SystemUser)
        {
            currentTx.Nonce = stateProvider.GetNonce(currentTx.SenderAddress!);
        }

        using ITxTracer tracer = receiptsTracer.StartNewTxTrace(currentTx);
        TransactionResult result = transactionProcessor.Execute(currentTx, receiptsTracer);
        receiptsTracer.EndTxTrace();
        return result;
    }

    public static TransactionResult ProcessTransaction<TGasPolicy>(this ITransactionProcessorAdapter transactionProcessor,
        Transaction currentTx,
        BlockReceiptsTracer receiptsTracer,
        ProcessingOptions processingOptions,
        IWorldState stateProvider,
        in IntrinsicGas<TGasPolicy> intrinsicGas)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        if (processingOptions.ContainsFlag(ProcessingOptions.LoadNonceFromState) && currentTx.SenderAddress != Address.SystemUser)
        {
            currentTx.Nonce = stateProvider.GetNonce(currentTx.SenderAddress!);
        }

        using ITxTracer tracer = receiptsTracer.StartNewTxTrace(currentTx);
        TransactionResult result = transactionProcessor.Execute(currentTx, receiptsTracer, in intrinsicGas);
        receiptsTracer.EndTxTrace();
        return result;
    }
}
