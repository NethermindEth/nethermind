using System.Collections.Generic;

namespace Nethermind.Pipeline
{
    public class Pipeline : IPipeline
    {
        public Pipeline(IEnumerable<IPipelineElement> elements)
        {
           Elements = new Stack<IPipelineElement>(elements); 
        }

        public Stack<IPipelineElement> Elements { get; }
    }
}