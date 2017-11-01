using System;
using System.Numerics;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;

namespace Nevermind.Evm
{
    public class EvmStack
    {
        public const int MaxSize = 1024;
        private readonly byte[][] _array = new byte[1024][];
        private readonly BigInteger?[] _intArray = new BigInteger?[1024];

        private int _head;

        public void Push(byte[] value)
        {
            if (ShouldLog.Evm)
            {
                Console.WriteLine($"  PUSH {Hex.FromBytes(value, true)}");
            }

            _intArray[_head] = null;
            _array[_head] = value;
            _head++;
            if (_head >= 1024)
            {
                throw new StackOverflowException();
            }
        }

        public void Push(BigInteger value)
        {
            if (ShouldLog.Evm)
            {
                Console.WriteLine($"  PUSH {value}");
            }

            _array[_head] = null;
            _intArray[_head] = value;
            _head++;
            if (_head >= 1024)
            {
                throw new StackOverflowException();
            }
        }

        public void PopLimbo()
        {
            if (_head == 0)
            {
                throw new StackUnderflowException();
            }

            _head--;
        }

        public void Dup(int depth)
        {
            _array[_head] = _array[_head - depth];
            _intArray[_head] = _intArray[_head - depth];
            _head++;
            if (_head >= 1024)
            {
                throw new StackOverflowException();
            }
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
            if (_head == 0)
            {
                throw new StackUnderflowException();
            }

            _head--;
            byte[] value = _array[_head];
            BigInteger? bigInteger = _intArray[_head];
            
            if (ShouldLog.Evm)
            {
                string valueSTring = value == null ? bigInteger.ToString() : Hex.FromBytes(value, true);
                Console.WriteLine($"  POP {valueSTring}");
            }

            if (value != null)
            {
                return value;
            }

            return bigInteger.Value.ToBigEndianByteArray();
        }

        public BigInteger PopInt(bool signed)
        {
            if (_head == 0)
            {
                throw new StackUnderflowException();
            }

            _head--;
            byte[] value = _array[_head];
            BigInteger? bigInteger = _intArray[_head];

            if (bigInteger.HasValue)
            {
                if (ShouldLog.Evm)
                {
                    string valueSTring = value == null ? bigInteger.ToString() : Hex.FromBytes(value, true);
                    Console.WriteLine($"  POP {valueSTring}");
                }

                if (signed)
                {
                    return bigInteger.Value.ToBigEndianByteArray().ToSignedBigInteger();
                }

                return bigInteger.Value;
            }

            BigInteger result = signed ? value.ToSignedBigInteger() : value.ToUnsignedBigInteger();

            if (ShouldLog.Evm)
            {
                Console.WriteLine($"  POP {result}");
            }
            
            return result;
        }
    }
}