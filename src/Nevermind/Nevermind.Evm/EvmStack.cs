using System;
using System.Collections.Generic;
using Nevermind.Core.Encoding;

namespace Nevermind.Evm
{
    public class EvmStack
    {
        private const bool IsLogging = false;

        public const int MaxSize = 1024;

        private readonly Stack<byte[]> _stack = new Stack<byte[]>(1024);

        public void Push(byte[] value)
        {
            if (IsLogging)
            {
                Console.WriteLine($"PUSH {Hex.FromBytes(value, true)}");
            }

            _stack.Push(value);
        }

        public byte[] Pop()
        {
            byte[] value = _stack.Pop();
            ;
            if (IsLogging)
            {
                Console.WriteLine($"POP {Hex.FromBytes(value, true)}");
            }

            return value;
        }
    }
}