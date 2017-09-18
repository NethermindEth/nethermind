using System.Numerics;

namespace Nevermind.Core.Sugar
{
    public static class IntExtensions
    {
        public static BigInteger Ether(this int @this)
        {
            return @this * Unit.Ether;
        }
    }
}