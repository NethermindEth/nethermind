using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Pipeline;

namespace Nethermind.Dsl.Pipeline
{
    public class PipelineElement<TIn, TOut> : IPipelineElement<TIn, TOut> 
    {
        public Action<TOut> Emit { private get; set; }
        public List<Func<TIn, bool>> Conditions { get => _conditions; }
        private List<Func<TIn, bool>> _conditions;
        private Func<TIn, TOut> _transformData;
        private readonly ILogger _logger;

        public PipelineElement(Func<TIn, bool> condition, Func<TIn, TOut> transformData, ILogger logger)
        {
            _conditions = new List<Func<TIn, bool>> { condition } ?? throw new ArgumentNullException(nameof(condition));
            _transformData = transformData ?? throw new ArgumentNullException(nameof(transformData));
            _logger = logger;
        }

        public void SubscribeToData(TIn data)
        {
            if(Emit == null)
            {
                return;
            }

            foreach(var condition in _conditions)
            {
                if (condition(data))
                {
                    var dataToEmit = _transformData(data);
                    Emit(dataToEmit);
                }
            }
        }

        public void AddCondition(Func<TIn, bool> condition)
        {
            _conditions.Add(condition);
        }
    }
}