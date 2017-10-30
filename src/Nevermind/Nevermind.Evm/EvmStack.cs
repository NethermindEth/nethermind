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

        public EvmStack(int callDepth)
        {
            CallDepth = callDepth;
        }

        public int CallDepth { get; private set; }

        public void Push(byte[] value)
        {
            if (ShouldLog.VM)
            {
                Console.WriteLine($"PUSH {Hex.FromBytes(value, true)}");
            }

            _intArray[CallDepth] = null;
            _array[CallDepth] = value;
            CallDepth++;
            if (CallDepth >= 1024)
            {
                throw new InvalidOperationException();
            }
        }

        public void Push(BigInteger value)
        {
            if (ShouldLog.VM)
            {
                Console.WriteLine($"PUSH {value}");
            }

            _array[CallDepth] = null;
            _intArray[CallDepth] = value;
            CallDepth++;
            if (CallDepth >= 1024)
            {
                throw new InvalidOperationException();
            }
        }

        public void PopLimbo()
        {
            if (CallDepth == 0)
            {
                throw new InvalidOperationException();
            }

            CallDepth--;
        }

        public void Dup(int depth)
        {
            _array[CallDepth] = _array[CallDepth - depth];
            _intArray[CallDepth] = _intArray[CallDepth - depth];
            CallDepth++;
            if (CallDepth >= 1024)
            {
                throw new InvalidOperationException();
            }
        }

        public void Swap(int depth)
        {
            byte[] bytes = _array[CallDepth - depth];
            BigInteger? intVal = _intArray[CallDepth - depth];

            _array[CallDepth - depth] = _array[CallDepth - 1];
            _intArray[CallDepth - depth] = _intArray[CallDepth - 1];

            _array[CallDepth - 1] = bytes;
            _intArray[CallDepth - 1] = intVal;
        }

        public byte[] PopBytes()
        {
            byte[] value = _array[CallDepth - 1];
            BigInteger? bigInteger = _intArray[CallDepth - 1];
            CallDepth--;

            if (ShouldLog.VM)
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
            byte[] value = _array[CallDepth - 1];
            BigInteger? bigInteger = _intArray[CallDepth - 1];
            CallDepth--;

            if (ShouldLog.VM)
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