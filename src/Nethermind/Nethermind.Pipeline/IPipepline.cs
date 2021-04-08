using System.Collections.Generic;

namespace Nethermind.Pipeline
{
    public interface IPipeline
    {
        Stack<IPipelineElement> Elements { get; }
    }

}