using System;
using Nethermind.Core;
using Nethermind.Pipeline;

namespace Nethermind.Dsl.Pipeline
{
    public class PipelineElement<TIn, TOut> : IPipelineElement<TIn, TOut> 
    {
        private Func<TIn, TOut> _transformData;
        private Func<TIn, bool> _condition;
        public Action<TOut> Emit { private get; set; }

        public PipelineElement(Func<TIn, bool> condition, Func<TIn, TOut> transformData)
        {
            _condition = condition; 
            _transformData = transformData;
        }

        public void SubscribeToData(TIn data)
        {
            if(_condition(data))
            {
                var dataToEmit = _transformData(data);
                Emit(dataToEmit);
            }
        }
    }
}