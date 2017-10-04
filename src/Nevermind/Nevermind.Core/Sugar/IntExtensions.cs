using System;
using System.Numerics;

namespace Nevermind.Core.Sugar
{
    public static class IntExtensions
    {
        public static BigInteger Ether(this int @this)
        {
            return @this * Unit.Ether;
        }

        public static BigInteger Wei(this int @this)
        {
            return @this * Unit.Wei;
        }

        public static byte[] ToBigEndianByteArray(this int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return bytes;
        }
    }
}