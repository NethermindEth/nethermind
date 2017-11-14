using System.IO;
using System.Numerics;

namespace Nevermind.Evm
{
    public class EvmMemory
    {
        private const int WordSize = 32;

        private static readonly byte[] EmptyBytes = new byte[0];
        private readonly MemoryStream _memory = new MemoryStream();

        public void SaveWord(BigInteger location, byte[] word)
        {
            _memory.Position = (long)location;
            if (word.Length < WordSize)
            {
                byte[] zeros = new byte[WordSize - word.Length];
                _memory.Write(zeros, 0, zeros.Length);
            }

            _memory.Write(word, 0, word.Length);
        }

        public void SaveByte(BigInteger location, byte[] value)
        {
            _memory.Position = (long)location;
            _memory.WriteByte(value[value.Length - 1]);
        }

        // TODO: move
        public static ulong Div32Ceiling(BigInteger length)
        {
            BigInteger result = BigInteger.DivRem(length, 32, out BigInteger rem);
            return (ulong)(result + (rem > 0 ? 1 : 0));
        }

        public void Save(BigInteger location, byte[] value)
        {
            _memory.Position = (long)location;
            _memory.Write(value, 0, value.Length);
        }

        public byte[] Load(BigInteger location)
        {
            byte[] buffer = new byte[WordSize];
            _memory.Position = (long)location;
            _memory.Read(buffer, 0, WordSize);
            return buffer;
        }

        public byte[] Load(BigInteger location, BigInteger length)
        {
            if (length == BigInteger.Zero)
            {
                return EmptyBytes;
            }

            long position = (long)location;

            byte[] buffer = new byte[(long)length];
            if (location <= _memory.Length)
            {
                _memory.Position = position;
                _memory.Read(buffer, 0, buffer.Length);
            }

            return buffer;
        }
    }
}