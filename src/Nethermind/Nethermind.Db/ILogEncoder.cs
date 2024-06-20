
using System;

namespace Nethermind.Db
{
    public interface ILogEncoder<T>
    {
        void Encode(Span<byte> bytes, T output);
    }
}
