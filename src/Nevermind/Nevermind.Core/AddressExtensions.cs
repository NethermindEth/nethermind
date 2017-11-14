using System.Numerics;
using Nevermind.Core.Sugar;

namespace Nevermind.Core
{
    public static class AddressExtensions
    {
        public static bool IsPrecompiled(this Address address)
        {
            BigInteger asInt = address.Hex.ToUnsignedBigInteger();
            return asInt > 0 && asInt < 4;
        }
    }
}