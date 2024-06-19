
using System;

namespace Nethermind.Db
{
    public interface ILogEncoder<T>
    {
        T Encode(Span<byte> bytes);
    }
}
