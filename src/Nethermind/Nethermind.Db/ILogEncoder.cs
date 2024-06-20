
using System;

namespace Nethermind.Db
{
    public interface ILogEncoder<I, O>
    {
<<<<<<< HEAD
        void Encode(Span<byte> bytes, T output);
=======
        void Encode(Span<I> bytes, O[] output);
>>>>>>> b9db80c16 (WIP FastPForEncoder)
    }
}
