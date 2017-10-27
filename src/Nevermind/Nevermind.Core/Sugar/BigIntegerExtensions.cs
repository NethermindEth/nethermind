using System.Numerics;

namespace Nevermind.Core.Sugar
{
    public static class BigIntegerExtensions
    {
        public static BigInteger Abs(this BigInteger @this)
        {
            return BigInteger.Abs(@this);
        }

        public static byte[] ToBigEndianByteArray(this BigInteger bigInteger, bool unsigned = true, int length = -1)
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