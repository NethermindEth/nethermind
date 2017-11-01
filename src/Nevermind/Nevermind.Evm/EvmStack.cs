using System;
using System.Numerics;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;

namespace Nevermind.Evm
{
    public class EvmStack
    {
        public const int MaxSize = 1024;
        private readonly byte[][] _array = new byte[MaxSize][];
        private readonly BigInteger?[] _intArray = new BigInteger?[MaxSize];

        private int _head;

        public void Reset()
        {
            _head = 0;
        }

        public void Push(byte[] value)
        {
            if (ShouldLog.Evm)
            {
                Console.WriteLine($"  PUSH {Hex.FromBytes(value, true)}");
            }

            _intArray[_head] = null;
            _array[_head] = value;
            _head++;
            if (_head >= MaxSize)
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
            if (_head >= MaxSize)
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
            if (_head >= MaxSize)
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

        public BigInteger PopInt(bool signed = false)
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

    // experimental
    public class EvmBitStack
    {
        public const int MaxSize = 1024;
        private readonly byte[][] _array = new byte[MaxSize][];
        private readonly BigInteger[] _intArray = new BigInteger[MaxSize];
        private readonly bool[] _isInt = new bool[1024];

        private int _head;

        public void Reset()
        {
            _head = 0;
        }

        public void Push(byte[] value)
        {
            //if (ShouldLog.Evm)
            //{
            //    Console.WriteLine($"  PUSH {Hex.FromBytes(value, true)}");
            //}

            _isInt[_head] = false;
            _array[_head] = value;
            _head++;
            if (_head >= MaxSize)
            {
                throw new StackOverflowException();
            }
        }

        public void Push(BigInteger value)
        {
            //if (ShouldLog.Evm)
            //{
            //    Console.WriteLine($"  PUSH {value}");
            //}

            _isInt[_head] = true;
            _intArray[_head] = value;
            _head++;
            if (_head >= MaxSize)
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
            if (_isInt[_head - depth])
            {
                _intArray[_head] = _intArray[_head - depth];
                _isInt[_head] = true;
            }
            else
            {
                _array[_head] = _array[_head - depth];
                _isInt[_head] = false;
            }

            _head++;
            if (_head >= MaxSize)
            {
                throw new StackOverflowException();
            }
        }

        public void Swap(int depth)
        {
            // can be faster
            bool isInt = _isInt[_head - depth];
            byte[] bytes = _array[_head - depth];
            BigInteger intVal = _intArray[_head - depth];

            _isInt[_head - depth] = _isInt[_head - 1];
            _array[_head - depth] = _array[_head - 1];
            _intArray[_head - depth] = _intArray[_head - 1];

            _isInt[_head - 1] = isInt;
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
            if (_isInt[_head])
            {
                return _intArray[_head].ToBigEndianByteArray();
            }

            return _array[_head];
        }

        public BigInteger PopInt(bool signed)
        {
            if (_head == 0)
            {
                throw new StackUnderflowException();
            }

            _head--;

            if (_isInt[_head])
            {
                return signed ? _intArray[_head].ToBigEndianByteArray().ToSignedBigInteger() : _intArray[_head];
            }

            return signed ? _array[_head].ToSignedBigInteger() : _array[_head].ToUnsignedBigInteger();
        }
    }
}