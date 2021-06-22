using System;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Pipeline;

#nullable enable
namespace Nethermind.Dsl.Pipeline.Sources
{
    public class ProcessedTransactionsSource<TOut> : IPipelineElement<TOut> where TOut : Transaction
    {
        public Action<TOut> Emit { private get; set; }
        private IBlockProcessor _blockProcessor;

        public ProcessedTransactionsSource(IBlockProcessor blockProcessor)
        {
            _blockProcessor = blockProcessor;
            _blockProcessor.TransactionProcessed += OnProcesedTransaction;
        }

        private void OnProcesedTransaction(object? sender, TxProcessedEventArgs args)
        {
            if(Emit != null)
            {
                Emit((TOut)args.Transaction);
            }
        }
    }
}