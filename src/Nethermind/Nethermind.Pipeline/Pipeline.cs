using System.Collections.Generic;

namespace Nethermind.Pipeline
{
    /// <summary> 
    /// Class used to store <see cref="IPipelineElement"/> collection.
    /// For creation use <see cref="IPipelineBuilder{TSource, TOutput}"/>.
    /// </summary>
    public class Pipeline : IPipeline
    {
        public Pipeline(IEnumerable<IPipelineElement> elements)
        {
           Elements = new Stack<IPipelineElement>(elements); 
        }

        public Stack<IPipelineElement> Elements { get; private set; }
    }
}
