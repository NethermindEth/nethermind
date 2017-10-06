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

            // remove leading sign zero
            if (result[0] == 0)
            {
                return result.Slice(1, result.Length - 1);
            }

            return result;
        }
    }
}