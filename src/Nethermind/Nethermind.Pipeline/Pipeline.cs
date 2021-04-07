using System;
using System.Collections.Generic;
using Nethermind.Pipeline.Publishers;
using Nethermind.PubSub;

namespace Nethermind.Pipeline
{
    public class Pipeline<T> : IPipeline<T>
    {
        public Stack<IPipelineElement<T>> Elements { get; }
        public ISource<T> Source { get; }

        public Pipeline(ISource<T> source)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public void AddElement(IPipelineElement<T> element)
        {
            var lastElement = Elements.Peek();
            if (lastElement == null)
            {
                Source.Emit = element.SubscribeToData;
            }
            else
            {
                lastElement.Emit = element.SubscribeToData;
            }

            Elements.Push(element);
        }

        public void RemoveLastElement()
        {
            Elements.Pop();
        }
    }
}