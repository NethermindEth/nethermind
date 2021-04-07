using System;

namespace Nethermind.Pipeline
{
    public interface ISource<T>
    {
        Action<T> Emit { set; }
    }
}