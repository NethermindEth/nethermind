using System;
using System.Numerics;
using Nevermind.Core.Sugar;

namespace Nevermind.Evm
{
    public class EvmMemory
    {
        private const int WordSize = 32;

        private byte[] _memory = new byte[0];

        private void Expand(int size)
        {
            Array.Resize(ref _memory, size);
        }

        private readonly BigInteger _activeWordsInMemory = 0;

        public BigInteger SaveWord(BigInteger location, byte[] word)
        {
            Save(location, Bytes.PadLeft(word, 32));
            BigInteger rem;
            BigInteger newActiveWords = BigInteger.Max(_activeWordsInMemory, BigInteger.DivRem(location + WordSize, WordSize, out rem));
            return newActiveWords + rem > 0 ? 1 : 0;
        }

        public BigInteger SaveByte(BigInteger location, byte[] value)
        {
            if (value.Length != 1)
            {
                throw new ArgumentException(nameof(value));
            }

            Save(location, value);

            BigInteger rem;
            BigInteger newActiveWords = BigInteger.Max(_activeWordsInMemory, BigInteger.DivRem(location + 1, WordSize, out rem));
            return newActiveWords + rem > 0 ? 1 : 0;
        }

        private void Save(BigInteger location, byte[] value)
        {
            if (_memory.Length < location + value.Length)
            {
                Expand((int) location + value.Length);
            }

            for (int i = 0; i < value.Length; i++)
            {
                _memory[(int) location + i] = value[i];
            }
        }

        public (byte[], BigInteger) Load(BigInteger location)
        {
            byte[] word = _memory.Slice((int)location, Math.Min(WordSize, _memory.Length - (int)location)).PadRight(32);
            BigInteger rem;
            BigInteger newActiveWords = BigInteger.Max(_activeWordsInMemory, BigInteger.DivRem(location + WordSize, WordSize, out rem));
            return (word, newActiveWords + rem > 0 ? 1 : 0);
        }

        public byte[] Load(BigInteger location, BigInteger length)
        {
            // TODO: harmful with big length values?
            return _memory.Slice((int)location, Math.Min((int)length, _memory.Length - (int)location)).PadRight((int)length);
        }
    }
}