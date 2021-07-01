using System.Collections.Generic;
using Nethermind.Pipeline.Publishers;

namespace Nethermind.Pipeline
{
    public class PipelineBuilder<TSource, TOutput> : IPipelineBuilder<TSource, TOutput>
    {
        private readonly Stack<IPipelineElement> _elements; 
        private readonly IPipelineElement<TOutput> _lastElement;

        public IPipelineElement LastElement => _lastElement;
        
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

        public IPipelineBuilder<TSource, TOutput> AddPublisher(IPublisher publisher)
        {
            _lastElement.Emit = publisher.SubscribeToData;
            _elements.Push((IPipelineElement) publisher);
            
            return this;
        }
        
        public IPipeline Build()
        {
            return new Pipeline(_elements);
        }
    }
}
