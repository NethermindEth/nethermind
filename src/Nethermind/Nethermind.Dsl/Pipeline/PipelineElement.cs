using System;
using Nethermind.Core;
using Nethermind.Pipeline;

namespace Nethermind.Dsl.Pipeline
{
    public class PipelineElement<TIn, TOut> : IPipelineElement<TIn, TOut> 
    {
        private Func<TIn, TOut> _condition;
        public Action<TOut> Emit { private get; set; }

        public PipelineElement(Func<TIn, TOut> condition)
        {
            _condition = condition; 
        }

        public void SubscribeToData(TIn data)
        {
            var filteredData = _condition(data);
            Emit((TOut)filteredData);
        }
    }
}