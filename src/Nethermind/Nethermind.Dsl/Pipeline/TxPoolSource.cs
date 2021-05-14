using System;
using Nethermind.Core;
using Nethermind.Pipeline;
using Nethermind.TxPool;

#nullable enable
namespace Nethermind.Dsl.Pipeline
{
    public class TxPoolSource<TOut> : IPipelineElement<TOut> where TOut : Transaction
    {
        private readonly ITxPool _txPool;

        public TxPoolSource(ITxPool txPool)
        {
            _txPool = txPool;
            _txPool.NewPending += OnNewPending;
        }

        public Action<TOut> Emit { private get; set; }

        private void OnNewPending(object? sender, TxEventArgs args)
        {
            if(Emit == null)
            {
                return; 
            }

            Emit((TOut)args.Transaction);
        }
    }
}