using System;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Taiko;

public class BlockInvalidTxExecutor(ITransactionProcessorAdapter txProcessor, IWorldState worldState) : IBlockProcessor.IBlockTransactionsExecutor
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
        TaikoPlugin.Logger.Warn($"#! Execution of {block.Transactions.Length} in block {block.Hash}({block.Number})");

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            Transaction tx = block.Transactions[i];
            using ITxTracer _ = receiptsTracer.StartNewTxTrace(tx);
            if (tx.Type == TxType.Blob)
            {
                // Skip blob transactions
                continue;
            }
            try
            {
                var res = _txProcessor.Execute(tx, in blkCtx, receiptsTracer);
                if (!res)
                // if the transaction was invalid, we ignore it and continue
                {
                    TaikoPlugin.Logger.Warn($"#! Unable to execute, {res}");
                    continue;
                }
            }
            catch(Exception e)
            {
                // sometimes invalid transactions can throw exceptions because
                // they are detected later in the processing pipeline
                TaikoPlugin.Logger.Warn($"#! Exception on execute, {e.Message} {e.StackTrace}");
                continue;
            }
            // only end the trace if the transaction was successful
            // so that we don't increment the receipt index for failed transactions
            receiptsTracer.EndTxTrace();
            TaikoPlugin.Logger.Warn($"#! Executed, {tx.Hash}");
            TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(i, tx, receiptsTracer.LastReceipt));
        }

        _worldState.Commit(spec, receiptsTracer);
        return [.. receiptsTracer.TxReceipts];
    }
}
