using System.Numerics;

namespace Nevermind.Core.Validators
{
    public abstract class Validator
    {
        private static readonly BigInteger P256 = BigInteger.Pow(2, 256);

        private static readonly BigInteger P5 = BigInteger.Pow(2, 5);

        public static bool IsInP256(BigInteger value)
        {
            return value > 0 && value < P256;
        }

        public static bool IsInP5(BigInteger value)
        {
            return value > 0 && value < P5;
        }
    }
}