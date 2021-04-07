using System;
using Nethermind.Core;
using Nethermind.Pipeline;

namespace MyPlugin
{
    public class TxPoolPipelineElement : IPipelineElement<Transaction>
    {
        public event EventHandler<Transaction> Emit;

        public void SubscribeToData<TIn>(object sender, TIn args)
        {
            Emit?.Invoke(sender, args);
        }
    }
}