using System;
using System.Collections.Generic;
using Nethermind.Core;
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
        }

        public void SubscribeToData(TIn data)
        {
            var block = data as Block;
            if(_logger.IsInfo) _logger.Info($"Recieved data in pipeline element. Block author: {block.Author}"); 
            foreach(var condition in _conditions)
            {
                if (condition(data))
                {
                    var dataToEmit = _transformData(data);
                    Emit(dataToEmit);
                }
                if(_logger.IsInfo) _logger.Info("Data did not match condition.");
            }
        }

        public void AddCondition(Func<TIn, bool> condition)
        {
            _conditions.Add(condition);
        }
    }
}