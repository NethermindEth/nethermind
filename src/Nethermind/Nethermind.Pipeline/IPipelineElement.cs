using System;

namespace Nethermind.Pipeline
{
    public interface IPipelineElement<T>
    {
       void SubscribeToData(T data); 
       Action<T> Emit { set; }
    }
}