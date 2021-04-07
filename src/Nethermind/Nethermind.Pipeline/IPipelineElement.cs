using System;

namespace Nethermind.Pipeline
{
    public interface IPipelineElement<T>
    {
       void SubscribeToData(object? sender, T data); 
       event EventHandler<T> Emit;
    }
}