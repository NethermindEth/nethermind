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

        // TODO: check if this change was needed, and confirm the outputLength default value of 32
        public static byte[] ToBigEndianByteArray(this BigInteger bigInteger, bool unsigned = true, int outputLength = -1)
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
        
        public static byte[] ToBigEndianByteArrayOld(this BigInteger bigInteger, bool unsigned = true, int length = -1)
        {
            byte[] fromBigInteger = bigInteger.ToByteArray();
            bool removeLeadingZero =
                unsigned
                && fromBigInteger.Length != 1
                && fromBigInteger[fromBigInteger.Length - 1] == 0;

            int desiredLength = length == -1
                ? fromBigInteger.Length - (removeLeadingZero ? 1 : 0)
                : length;

            byte[] result = new byte[desiredLength];
            for (int i = 0; i < fromBigInteger.Length - (removeLeadingZero ? 1 : 0); i++)
            {
                result[desiredLength - 1 - i] = fromBigInteger[i];
            }

            if (bigInteger.Sign < 0)
            {
                for (int i = 0; i < desiredLength - fromBigInteger.Length - (removeLeadingZero ? 1 : 0); i++)
                {
                    result[i] = 0xff;
                }
            }

            return result;
        }
    }
}