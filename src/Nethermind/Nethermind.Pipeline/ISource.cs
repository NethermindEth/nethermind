using System;

namespace Nethermind.Pipeline
{
    public interface ISource<TOut>
    {
        event EventHandler<TOut>? Emit;
    }
}