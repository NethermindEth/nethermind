using System;
using Nethermind.Core;
using Nethermind.Pipeline;
using Nethermind.TxPool;

namespace MyPlugin
{
    public class PipelineSource<T> : ISource<T> where T : Transaction
    {
        public ITxPool _txPool { get; set; }
        public event EventHandler<T> Emit;

        public PipelineSource(ITxPool txPool)
        {
            _txPool = txPool;
            _txPool.NewPending += OnNewPending;
        }

        public void OnNewPending(object? sender, TxEventArgs args)
        {
            Emit?.Invoke(this, (T)args.Transaction);
        }
    }
}