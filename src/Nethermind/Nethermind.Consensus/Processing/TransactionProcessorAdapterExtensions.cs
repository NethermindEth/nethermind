// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

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
            // Use GetAccountDirect to read from block-level state (_blockChanges) without
            // populating _intraTxCache. This prevents stale cache entries that would break
            // the direct transfer path: if _intraTxCache is populated here and the direct
            // path then writes updated state to _blockChanges (bypassing Commit which normally
            // clears _intraTxCache), subsequent transactions would read stale state.
            Account? directAccount = stateProvider.GetAccountDirect(currentTx.SenderAddress!);
            currentTx.Nonce = directAccount?.Nonce ?? UInt256.Zero;
        }

        using ITxTracer tracer = receiptsTracer.StartNewTxTrace(currentTx);
        TransactionResult result = transactionProcessor.Execute(currentTx, receiptsTracer);
        receiptsTracer.EndTxTrace();
        return result;
    }
}
