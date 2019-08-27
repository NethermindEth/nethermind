/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles
{
    public static class AddressExtensions
    {
        public static bool IsPrecompiled(this Address address, IReleaseSpec releaseSpec)
        {
            if(address[0] != 0)
            {
                return false;
            }
            
            BigInteger asInt = address.Bytes.ToUnsignedBigInteger();
            if (asInt == 0 || asInt > 9)
            {
                return false;
            }

            if (asInt > 0 && asInt <= 4)
            {
                return true;
            }

            if (asInt == 5)
            {
                return releaseSpec.IsEip198Enabled;
            }

            if (asInt == 6 || asInt == 7)
            {
                return releaseSpec.IsEip196Enabled;
            }
            
            if (asInt == 8)
            {
                return releaseSpec.IsEip197Enabled;
            }

            return asInt == 9 && releaseSpec.IsEip152Enabled;
        }
    }
}