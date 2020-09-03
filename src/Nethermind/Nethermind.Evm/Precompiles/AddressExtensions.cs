//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles
{
    public static class AddressExtensions
    {
        private static byte[] _nineteenZeros = new byte[19];

        public static bool IsPrecompile(this Address address, IReleaseSpec releaseSpec)
        {
            if (!Bytes.AreEqual(address.Bytes.AsSpan(0, 19), _nineteenZeros))
            {
                return false;
            }
           
            int precompileCode = address[19];
            return precompileCode switch
            {
                1 => true,
                2 => true,
                3 => true,
                4 => true,
                5 => releaseSpec.IsEip198Enabled,
                6 => releaseSpec.IsEip196Enabled,
                7 => releaseSpec.IsEip196Enabled,
                8 => releaseSpec.IsEip197Enabled,
                9 => releaseSpec.IsEip152Enabled,
                10 => releaseSpec.IsEip2537Enabled,
                11 => releaseSpec.IsEip2537Enabled,
                12 => releaseSpec.IsEip2537Enabled,
                13 => releaseSpec.IsEip2537Enabled,
                14 => releaseSpec.IsEip2537Enabled,
                15 => releaseSpec.IsEip2537Enabled,
                16 => releaseSpec.IsEip2537Enabled,
                17 => releaseSpec.IsEip2537Enabled,
                18 => releaseSpec.IsEip2537Enabled,
                _ => false
            };
        }
    }
}