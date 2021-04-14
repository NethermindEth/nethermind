using System.Collections.Generic;

namespace Nethermind.Pipeline
{
    public class PipelineBuilder<TSource, TOutput> : IPipelineBuilder<TSource, TOutput>
    {
        private readonly Stack<IPipelineElement> _elements; 
        private readonly IPipelineElement<TOutput> _lastElement;
        
        public PipelineBuilder(IPipelineElement<TOutput> sourceElement)
        {
            _lastElement = sourceElement; 
            _elements = new Stack<IPipelineElement>();
            _elements.Push(_lastElement);
        }

        private PipelineBuilder(IPipelineElement<TOutput> element, IEnumerable<IPipelineElement> elements)
        {
            _lastElement = element;
            _elements = new Stack<IPipelineElement>(elements);
            _elements.Push(element);
        }

        public IPipelineBuilder<TSource, TOut> AddElement<TOut>(IPipelineElement<TOutput, TOut> element)
        {
            _lastElement.Emit = element.SubscribeToData;
            return new PipelineBuilder<TSource, TOut>(element, _elements);
        }

        public IPipeline Build()
        {
            return new Pipeline(_elements);
        }
    }
}
