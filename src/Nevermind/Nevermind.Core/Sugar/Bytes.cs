using System;
using System.Numerics;

namespace Nevermind.Core.Sugar
{
    public static class Bytes
    {
        public enum Endianness
        {
            Big,
            Little
        }

        public static unsafe bool UnsafeCompare(byte[] a1, byte[] a2)
        {
            if (a1 == a2)
            {
                return true;
            }
            if (a1 == null || a2 == null || a1.Length != a2.Length)
            {
                return false;
            }
            fixed (byte* p1 = a1, p2 = a2)
            {
                byte* x1 = p1, x2 = p2;
                int l = a1.Length;
                for (int i = 0; i < l / 8; i++, x1 += 8, x2 += 8)
                {
                    if (*((long*) x1) != *((long*) x2))
                    {
                        return false;
                    }
                }
                if ((l & 4) != 0)
                {
                    if (*((int*) x1) != *((int*) x2))
                    {
                        return false;
                    }
                    x1 += 4;
                    x2 += 4;
                }
                if ((l & 2) != 0)
                {
                    if (*((short*) x1) != *((short*) x2))
                    {
                        return false;
                    }
                    x1 += 2;
                    x2 += 2;
                }
                if ((l & 1) != 0)
                {
                    if (*x1 != *x2)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        public static byte[] Merge(byte prefix, byte[] bytes)
        {
            byte[] result = new byte[1 + bytes.Length];
            result[0] = prefix;
            Buffer.BlockCopy(bytes, 0, result, 1, bytes.Length);
            return result;
        }

        public static byte[] Merge(byte prefix, byte[] part1, byte[] part2)
        {
            byte[] output = new byte[1 + part1.Length + part2.Length];
            output[0] = prefix;
            Buffer.BlockCopy(part1, 0, output, 1, part1.Length);
            Buffer.BlockCopy(part2, 0, output, 1 + part1.Length, part2.Length);
            return output;
        }

        public static byte[] PadLeft(byte[] bytes, int length)
        {
            byte[] result = new byte[length];
            Buffer.BlockCopy(bytes, 0, result, length - bytes.Length, bytes.Length);
            return result;
        }

        public static byte[] PadRight(byte[] bytes, int length)
        {
            byte[] result = new byte[length];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        public static byte[] Merge(byte[] firstPart, byte[] secondPart)
        {
            byte[] result = new byte[firstPart.Length + secondPart.Length];
            Buffer.BlockCopy(firstPart, 0, result, 0, firstPart.Length);
            Buffer.BlockCopy(secondPart, 0, result, firstPart.Length, secondPart.Length);
            return result;
        }

        public static byte[] Merge(byte[] bytes, byte suffix)
        {
            byte[] result = new byte[bytes.Length + 1];
            result[result.Length - 1] = suffix;
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        public static byte[] Reverse(byte[] bytes)
        {
            byte[] result = new byte[bytes.Length];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            Array.Reverse(result);
            return result;
        }

        public static BigInteger ToUnsignedBigInteger(this byte[] bytes, Endianness endianness = Endianness.Big)
        {
            if (BitConverter.IsLittleEndian && endianness == Endianness.Big)
            {
                bytes = Reverse(bytes);
            }

            byte[] unsigned = Merge(bytes, 0);
            return new BigInteger(unsigned);
        }

        public static BigInteger ToSignedBigInteger(this byte[] bytes, Endianness endianness = Endianness.Big)
        {
            if (BitConverter.IsLittleEndian && endianness == Endianness.Big)
            {
                bytes = Reverse(bytes);
            }

            return new BigInteger(bytes);
        }

        public static ulong ToUInt64(this byte[] bytes, Endianness endianness = Endianness.Big)
        {
            if (BitConverter.IsLittleEndian && endianness == Endianness.Big)
            {
                Array.Reverse(bytes);
            }

            bytes = PadRight(bytes, 8);
            ulong result = BitConverter.ToUInt64(bytes, 0);
            return result;
        }
    }
}