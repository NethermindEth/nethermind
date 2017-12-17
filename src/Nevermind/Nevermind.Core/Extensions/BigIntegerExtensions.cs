using System;
using System.Numerics;

namespace Nevermind.Core.Extensions
{
    public static class BigIntegerExtensions
    {
        public static BigInteger Abs(this BigInteger @this)
        {
            return BigInteger.Abs(@this);
        }

        public static byte[] ToBigEndianByteArray(this BigInteger bigInteger, int outputLength = -1)
        {
            byte[] fromBigInteger = bigInteger.ToByteArray();
            int trailingZeros = fromBigInteger.TrailingZerosCount();
            if (fromBigInteger.Length == trailingZeros)
            {
                return new byte[outputLength == -1 ? 1 : outputLength];
            }

            byte[] result = new byte[fromBigInteger.Length - trailingZeros];
            for (int i = 0; i < result.Length; i++)
            {
                result[fromBigInteger.Length - trailingZeros - 1 - i] = fromBigInteger[i];
            }

            if (bigInteger.Sign < 0 && outputLength != -1)
            {
                byte[] newResult = new byte[outputLength];
                Buffer.BlockCopy(result, 0, newResult, outputLength - result.Length, result.Length);
                for (int i = 0; i < outputLength - result.Length; i++)
                {
                    newResult[i] = 0xff;
                }

                return newResult;
            }

            if (outputLength != -1)
            {
                return result.PadLeft(outputLength);
            }

            return result;
        }
    }
}