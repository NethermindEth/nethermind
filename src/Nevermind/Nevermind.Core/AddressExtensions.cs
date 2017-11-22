using System.Numerics;
using Nevermind.Core.Sugar;

namespace Nevermind.Core
{
    public static class AddressExtensions
    {
        public static bool IsPrecompiled(this Address address, IProtocolSpecification protocolSpecification)
        {
            BigInteger asInt = address.Hex.ToUnsignedBigInteger();
            if (asInt == 0 || asInt > 8)
            {
                return false;
            }

            if (asInt > 0 && asInt < 4)
            {
                return true;
            }

            if (asInt == 5)
            {
                return protocolSpecification.IsEip198Enabled;
            }

            if (asInt == 6 || asInt == 7)
            {
                return protocolSpecification.IsEip196Enabled;
            }

            return asInt == 8 && protocolSpecification.IsEip197Enabled;
        }
    }
}