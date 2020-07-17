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
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto.Bls;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Precompiles.Bls.Shamatar
{
    /// <summary>
    /// https://eips.ethereum.org/EIPS/eip-2537
    /// </summary>
    public class PairingPrecompile : IPrecompile
    {
        private const int PairSize = 384;

        private PairingPrecompile() { }
        
        public Address Address { get; } = Address.FromNumber(16);
        
        public static IPrecompile Instance = new PairingPrecompile();

        public long BaseGasCost(IReleaseSpec releaseSpec) => 115000L;

        public long DataGasCost(byte[] inputData, IReleaseSpec releaseSpec)
        {
            if (inputData == null)
            {
                return 0L;
            }

            return 23000L * (inputData.Length / PairSize);
        }

        public (byte[], bool) Run(byte[] inputData)
        {
            inputData ??= Array.Empty<byte>();
            if (inputData.Length % PairSize > 0)
            {
                // note that it will not happen in case of null / 0 length
                return (Array.Empty<byte>(), false);
            }

            (byte[], bool) result;
            
            Span<byte> output = stackalloc byte[32];
            bool success = ShamatarLib.BlsPairing(inputData, output);
            if (success)
            {
                result = (output.ToArray(), true);
            }
            else
            {
                result = (Array.Empty<byte>(), false);
            }

            return result;
        }
    }
}