using System.Numerics;
using Nevermind.Core.Extensions;
using Nevermind.Core.Potocol;

namespace Nevermind.Core
{
    public static class AddressExtensions
    {
        public static bool IsPrecompiled(this Address address, IEthereumRelease ethereumRelease)
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
                return ethereumRelease.IsEip198Enabled;
            }

            if (asInt == 6 || asInt == 7)
            {
                return ethereumRelease.IsEip196Enabled;
            }

            return asInt == 8 && ethereumRelease.IsEip197Enabled;
        }
    }
}