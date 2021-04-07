using Nethermind.Pipeline.Publishers;
using Nethermind.PubSub;
using System.Collections.Generic;

namespace Nethermind.Pipeline
{
    public interface IPipeline<T>
    {
        ISource<T> Source { get; }
        Stack<IPipelineElement<T>> Elements { get; }
        void AddElement(IPipelineElement<T> element);
        void RemoveLastElement();
    }
}