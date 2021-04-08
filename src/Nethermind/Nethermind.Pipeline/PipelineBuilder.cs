using System.Collections.Generic;

namespace Nethermind.Pipeline
{
    public class PipelineBuilder<TSource, TOutput> : IPipelineBuilder<TSource, TOutput>
    {
        private readonly Stack<IPipelineElement> Elements; 
        private readonly IPipelineElement<TOutput> LastElement;
        
        public PipelineBuilder(IPipelineElement<TOutput> sourceElement)
        {
            LastElement = sourceElement; 
            Elements = new Stack<IPipelineElement>();
            Elements.Push(LastElement);
        }

        private PipelineBuilder(IPipelineElement<TOutput> element, IEnumerable<IPipelineElement> elements)
        {
            LastElement = element;
            Elements = new Stack<IPipelineElement>(elements);
            Elements.Push(element);
        }

        public IPipelineBuilder<TSource, TOut> AddElement<TOut>(IPipelineElement<TOutput, TOut> element)
        {
            LastElement.Emit = element.SubscribeToData;
            return new PipelineBuilder<TSource, TOut>(element, Elements);
        }

        public IPipeline Build()
        {
            return new Pipeline(Elements);
        }
    }
}