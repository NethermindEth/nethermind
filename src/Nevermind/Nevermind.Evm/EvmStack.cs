using System;
using System.Numerics;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;

namespace Nevermind.Evm
{
    public class EvmStack
    {
        private int _head;

        private readonly byte[][] _array = new byte[1024][];
        private readonly BigInteger?[] _intArray = new BigInteger?[1024];

        private const bool IsLogging = false;

        public const int MaxSize = 1024;

        public void Push(byte[] value)
        {
            if (IsLogging)
            {
                Console.WriteLine($"PUSH {Hex.FromBytes(value, true)}");
            }

            _intArray[_head] = null;
            _array[_head++] = value;
        }

        public void Push(BigInteger value)
        {
            if (IsLogging)
            {
                Console.WriteLine($"PUSH {value}");
            }

            _array[_head] = null;
            _intArray[_head++] = value;
        }

        public void PopLimbo()
        {
            if (_head == 0)
            {
                throw new InvalidOperationException();
            }

            _head--;
        }

        public void Dup(int depth)
        {
            _array[_head] = _array[_head - depth];
            _intArray[_head] = _intArray[_head - depth];
            _head++;
        }

        public void Swap(int depth)
        {
            byte[] bytes = _array[_head - depth];
            BigInteger? intVal = _intArray[_head - depth];

            _array[_head - depth] = _array[_head - 1];
            _intArray[_head - depth] = _intArray[_head - 1];

            _array[_head - 1] = bytes;
            _intArray[_head - 1] = intVal;
        }

        public byte[] PopBytes()
        {
            byte[] value = _array[_head - 1];
            BigInteger? bigInteger = _intArray[_head - 1];
            _head--;

            if (IsLogging)
            {
                string valueSTring = value == null ? bigInteger.ToString() : Hex.FromBytes(value, true);
                Console.WriteLine($"POP {valueSTring}");
            }

            if (value != null)
            {
                return value;
            }

            return bigInteger.Value.ToBigEndianByteArray();
        }

        public BigInteger PopInt(bool signed)
        {
            byte[] value = _array[_head - 1];
            BigInteger? bigInteger = _intArray[_head - 1];
            _head--;

            if (IsLogging)
            {
                string valueSTring = value == null ? bigInteger.ToString() : Hex.FromBytes(value, true);
                Console.WriteLine($"POP {valueSTring}");
            }

            if (bigInteger.HasValue)
            {
                if (signed)
                {
                    return bigInteger.Value.ToBigEndianByteArray().ToSignedBigInteger();
                }

                return bigInteger.Value;
            }

            return signed ? value.ToSignedBigInteger() : value.ToUnsignedBigInteger();
        }
    }
}