using System;
using System.Numerics;

namespace Nevermind.Core.Sugar
{
    public static class BigIntegerExtensions
    {
        public static byte[] ToBigEndianByteArray(this BigInteger bigInteger)
        {
            byte[] result = bigInteger.ToByteArray();
            Array.Reverse(result);
            return result;
        }
    }
}