using System;
using System.IO;
using System.Numerics;
using Nevermind.Core.Sugar;

namespace Nevermind.Evm
{
    public class EvmMemory
    {
        private MemoryStream memory = new MemoryStream();

        private const int WordSize = 32;

        private byte[] _memory = new byte[0];

        private void Expand(int size)
        {
            Array.Resize(ref _memory, size);
        }

        public void SaveWord(BigInteger location, byte[] word)
        {
            Save(location, word.PadLeft(32));
        }

        public void SaveByte(BigInteger location, byte[] value)
        {
            Save(location, new byte[] { value[value.Length - 1] });
        }

        // TODO: move
        public static ulong Div32Ceiling(BigInteger length)
        {
            BigInteger rem;
            BigInteger result = BigInteger.DivRem(length, 32, out rem);
            return (ulong)(result + (rem > 0 ? 1 : 0));
        }

        public void Save(BigInteger location, byte[] value)
        {
            if (_memory.Length < location + value.Length)
            {
                Expand((int)location + value.Length);
            }

            for (int i = 0; i < value.Length; i++)
            {
                _memory[(int)location + i] = value[i];
            }
        }

        public byte[] Load(BigInteger location)
        {
            return Load(location, WordSize);
        }

        public byte[] Load(BigInteger location, BigInteger length, bool allowInvalidLocations = true)
        {
            if (length == BigInteger.Zero)
            {
                return new byte[0];
            }

            if (location > _memory.Length)
            {
                if (allowInvalidLocations)
                {
                    return new byte[(int)length];
                }

                throw new MemoryAccessException();
            }

            byte[] bytes = _memory.Slice((int)location, (int)BigInteger.Max(0, BigInteger.Min(length, _memory.Length - location)))
                .PadRight((int)length);
            return bytes;
        }
    }
}