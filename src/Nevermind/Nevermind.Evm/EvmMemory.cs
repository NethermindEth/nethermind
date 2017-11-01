using System;
using System.Numerics;
using Nevermind.Core.Sugar;

namespace Nevermind.Evm
{
    public class EvmMemory
    {
        private const int WordSize = 32;

        private ulong _activeWordsInMemory = 0;

        private byte[] _memory = new byte[0];

        private void Expand(int size)
        {
            Array.Resize(ref _memory, size);
        }

        public ulong SaveWord(BigInteger location, byte[] word)
        {
            return Save(location, word.PadLeft(32));
        }

        public ulong SaveByte(BigInteger location, byte[] value)
        {
            return Save(location, new byte[] {value[value.Length - 1]});
        }

        // TODO: move
        public static ulong Div32Ceiling(BigInteger length)
        {
            BigInteger rem;
            BigInteger result = BigInteger.DivRem(length, 32, out rem);
            return (ulong)(result + (rem > 0 ? 1 : 0));
        }

        public ulong Save(BigInteger location, byte[] value)
        {
            if (_memory.Length < location + value.Length)
            {
                Expand((int)location + value.Length);
            }

            for (int i = 0; i < value.Length; i++)
            {
                _memory[(int)location + i] = value[i];
            }

            _activeWordsInMemory = Math.Max(_activeWordsInMemory, Div32Ceiling(location + value.Length));
            return _activeWordsInMemory;
        }

        public (byte[], ulong) Load(BigInteger location)
        {
            return Load(location, WordSize);
        }

        public (byte[], ulong) Load(BigInteger location, BigInteger length, bool allowInvalidLocations = true)
        {
            if (length == BigInteger.Zero)
            {
                return (new byte[0], _activeWordsInMemory);
            }

            _activeWordsInMemory = Math.Max(_activeWordsInMemory, Div32Ceiling(location + length));

            if (allowInvalidLocations && location > _memory.Length)
            {
                return (new byte[(int)length], _activeWordsInMemory);
            }

            byte[] bytes = _memory.Slice((int)location, (int)BigInteger.Max(0, BigInteger.Min(length, _memory.Length - location)))
                .PadRight((int)length);
            return (bytes, _activeWordsInMemory);
        }
    }
}