//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Core.Specs;
using Nethermind.Crypto.Bls;

namespace Nethermind.Evm.Precompiles.Bls.Shamatar
{
    /// <summary>
    /// https://eips.ethereum.org/EIPS/eip-2537
    /// </summary>
    public class G2AddPrecompile : IPrecompile
    {
        public static IPrecompile Instance = new G2AddPrecompile();

        private G2AddPrecompile()
        {
        }

        public Address Address { get; } = Address.FromNumber(13);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return 4500L;
        }

        public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            return 0L;
        }

        public (ReadOnlyMemory<byte>, bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {  
            const int expectedInputLength = 8 * BlsParams.LenFp;
            if (inputData.Length != expectedInputLength)
            {
                return (Array.Empty<byte>(), false); 
            }
            
            // Span<byte> inputDataSpan = stackalloc byte[expectedInputLength];
            // inputData.PrepareEthInput(inputDataSpan);

            (byte[], bool) result;
            
            Span<byte> output = stackalloc byte[4 * BlsParams.LenFp];
            bool success = ShamatarLib.BlsG2Add(inputData.Span, output);
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
