using System.Numerics;

namespace Nevermind.Core.Sugar
{
    public static class DecimalExtensions
    {
        public static BigInteger Ether(this BigInteger @this)
        {
            return @this * Unit.Ether;
        }
    }
}