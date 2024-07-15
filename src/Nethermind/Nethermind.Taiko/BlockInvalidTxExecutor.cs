using System;
using System.Collections.Generic;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Taiko;

public class BlockInvalidTxExecutor(ITransactionProcessorAdapter txProcessor, IWorldState worldState, IEthereumEcdsa ethereumEcdsa) : IBlockProcessor.IBlockTransactionsExecutor
{
    private readonly IWorldState _worldState = worldState;
    private readonly ITransactionProcessorAdapter _txProcessor = txProcessor;

    public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec)
    {
        if (block.Transactions.Length == 0)
        {
            if (block.IsGenesis)
                return [];

            throw new ArgumentException("Block must contain at least the anchor transaction");
        }

        block.Transactions[0].IsAnchorTx = true;

        BlockExecutionContext blkCtx = new(block.Header);

        List<Transaction> correctTransactions = [];

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            Snapshot snap = _worldState.TakeSnapshot();
            Transaction tx = block.Transactions[i];

            if (tx.Type == TxType.Blob)
            {
                // Skip blob transactions
                continue;
            }

            tx.SenderAddress ??= ethereumEcdsa.RecoverAddress(tx);

            using ITxTracer _ = receiptsTracer.StartNewTxTrace(tx);

            try
            {
                if (!_txProcessor.Execute(tx, in blkCtx, receiptsTracer))
                // if the transaction was invalid, we ignore it and continue
                {
                    _worldState.Restore(snap);
                    continue;
                }
            }
            catch
            {
                // sometimes invalid transactions can throw exceptions because
                // they are detected later in the processing pipeline
                _worldState.Restore(snap);
                continue;
            }
            // only end the trace if the transaction was successful
            // so that we don't increment the receipt index for failed transactions
            receiptsTracer.EndTxTrace();
            TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(i, tx, receiptsTracer.LastReceipt));
            correctTransactions.Add(tx);
        }

        block.TrySetTransactions([.. correctTransactions]);
        return [.. receiptsTracer.TxReceipts];
    }
}
