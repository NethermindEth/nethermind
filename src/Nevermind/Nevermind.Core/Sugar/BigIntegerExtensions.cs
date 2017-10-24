using System;
using System.Numerics;

namespace Nevermind.Core.Sugar
{
    public static class BigIntegerExtensions
    {
        public static BigInteger Abs(this BigInteger @this)
        {
            return BigInteger.Abs(@this);
        }

        public static byte[] ToBigEndianByteArray(this BigInteger bigInteger, bool unsigned = true)
        {
            byte[] result = bigInteger.ToByteArray();
            Array.Reverse(result);

            // remove leading sign zero
            if (unsigned && result[0] == 0)
            {
                return result.Slice(1, result.Length - 1);
            }

            return result;
        }
    }
}