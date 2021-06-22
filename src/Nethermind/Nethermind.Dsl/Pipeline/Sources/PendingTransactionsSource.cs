using System;
using Nethermind.Core;
using Nethermind.Pipeline;
using Nethermind.TxPool;

#nullable enable
namespace Nethermind.Dsl.Pipeline.Sources
{
    public class PendingTransactionsSource<TOut> : IPipelineElement<TOut> where TOut : Transaction
    {
        private readonly ITxPool _txPool;

        public PendingTransactionsSource(ITxPool txPool)
        {
            _txPool = txPool;
            _txPool.NewPending += OnNewPending;
        }

        public Action<TOut>? Emit { private get; set; }

        private void OnNewPending(object? sender, TxEventArgs args)
        {
            Emit?.Invoke((TOut)args.Transaction);
        }
    }
}
