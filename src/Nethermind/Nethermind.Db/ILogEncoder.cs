
using System;

namespace Nethermind.Db
{
    public interface ILogEncoder<I, O>
    {
        void Encode(Span<I> bytes, O[] output);
    }
}
