using System;

namespace Nethermind.Pipeline
{
    public interface IPipelineElement
    {
    }

    public interface IPipelineElement<TOut> : IPipelineElement
    {
       Action<TOut> Emit { set; }
    }

    public interface IPipelineElement<TIn, TOut> : IPipelineElement<TOut>
    {
       void SubscribeToData(TIn data); 
    }
}