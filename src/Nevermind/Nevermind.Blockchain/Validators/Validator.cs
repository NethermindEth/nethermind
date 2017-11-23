using System.Numerics;

namespace Nevermind.Blockchain.Validators
{
    public abstract class Validator
    {
        private static readonly BigInteger P256 = BigInteger.Pow(2, 256);

        private static readonly BigInteger P5 = BigInteger.Pow(2, 5);

        public static bool IsInP256(BigInteger value)
        {
            return value >= BigInteger.Zero && value < P256;
        }

        public static bool IsInP5(BigInteger value)
        {
            return value >= BigInteger.Zero && value < P5;
        }
    }
}