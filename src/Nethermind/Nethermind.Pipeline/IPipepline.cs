using System.Collections.Generic;

namespace Nethermind.Pipeline
{
    /// <summary> 
    /// Interface used for storing <see cref="IPipelineElement"/> collection, implemented in <see cref="Pipeline"/>
    /// </summary> 
    public interface IPipeline
    {
        Stack<IPipelineElement> Elements { get; }
    }
}