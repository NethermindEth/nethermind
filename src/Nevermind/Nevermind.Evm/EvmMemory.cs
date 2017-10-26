using System;
using System.Numerics;
using Nevermind.Core.Sugar;

namespace Nevermind.Evm
{
    public class EvmMemory
    {
        private const int WordSize = 32;

        private BigInteger _activeWordsInMemory = 0;

        private byte[] _memory = new byte[0];

        private void Expand(int size)
        {
            Array.Resize(ref _memory, size);
        }

        public BigInteger SaveWord(BigInteger location, byte[] word)
        {
            return Save(location, word.PadLeft(32));
        }

        public BigInteger SaveByte(BigInteger location, byte[] value)
        {
            if (value.Length != 1)
            {
                throw new ArgumentException(nameof(value));
            }

            return Save(location, value);
        }

        public static BigInteger Div32Ceiling(BigInteger length)
        {
            BigInteger rem;
            BigInteger result = BigInteger.DivRem(length, 32, out rem);
            return result + (rem > 0 ? 1 : 0);
        }

        public BigInteger Save(BigInteger location, byte[] value)
        {
            if (_memory.Length < location + value.Length)
            {
                Expand((int) location + value.Length);
            }

            for (int i = 0; i < value.Length; i++)
            {
                _memory[(int) location + i] = value[i];
            }

            _activeWordsInMemory = BigInteger.Max(_activeWordsInMemory, Div32Ceiling(location + value.Length));
            return _activeWordsInMemory;
        }

        public (byte[], BigInteger) Load(BigInteger location)
        {
            return Load(location, WordSize);
        }

        public (byte[], BigInteger) Load(BigInteger location, BigInteger length)
        {
            byte[] bytes = _memory.Slice((int) location, Math.Min((int) length, _memory.Length - (int) location))
                .PadRight((int) length);
            _activeWordsInMemory = BigInteger.Max(_activeWordsInMemory, Div32Ceiling(location + length));
            return (bytes, _activeWordsInMemory);
        }
    }
}