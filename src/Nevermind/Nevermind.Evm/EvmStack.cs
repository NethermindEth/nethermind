using System.Collections.Generic;
using System.Numerics;

namespace Nevermind.Evm
{
    public class EvmStack
    {
        public const int MaxSize = 1024;

        private readonly Stack<byte[]> _stack = new Stack<byte[]>(1024);

        public void Push(byte[] bigInteger)
        {
            // check size
            _stack.Push(bigInteger);
        }

        public byte[] Pop()
        {
            return _stack.Pop();
        }

        public byte[] Peek()
        {
            return _stack.Peek();
        }
    }
}