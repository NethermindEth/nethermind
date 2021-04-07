using System;
using System.Collections.Generic;

namespace Nethermind.Pipeline
{
    public class Pipeline<T> : IPipeline<T>
    {
        public Stack<IPipelineElement<T>> Elements { get; }
        public ISource<T> Source { get; }
        public IPipelinePublisher<T> Publisher { get; }

        public Pipeline(ISource<T> source, IPipelinePublisher<T> publisher)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            Publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        }

        public void AddElement(IPipelineElement<T> element)
        {
            var lastElement = Elements.Peek();
            if (lastElement == null)
            {
                Source.Emit += element.SubscribeToData;
            }
            else
            {
                lastElement.Emit += element.SubscribeToData;
            }

            Elements.Push(element);
        }

        public void RemoveLastElement()
        {
            Elements.Pop();
        }
    }
}