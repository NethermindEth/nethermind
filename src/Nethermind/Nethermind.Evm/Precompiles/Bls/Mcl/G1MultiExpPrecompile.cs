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
using Nethermind.Crypto.Bls;

namespace Nethermind.Evm.Precompiles.Bls.Mcl
{
    /// <summary>
    /// https://eips.ethereum.org/EIPS/eip-2537
    /// </summary>
    public class G1MultiExpPrecompile : IPrecompile
    {
        public static IPrecompile Instance = new G1MultiExpPrecompile();

        private G1MultiExpPrecompile()
        {
        }

        public Address Address { get; } = Address.FromNumber(12);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return 0L;
        }

        public long DataGasCost(Span<byte> inputData, IReleaseSpec releaseSpec)
        {
            int k = inputData.Length / 192;
            return 12000L * k * Discount.For(k) / 1000;
        }

        private const int ItemSize = 160;
        
        public PrecompileResult Run(Span<byte> inputData)
        {
            if (inputData.Length % ItemSize > 0)
            {
                // note that it will not happen in case of null / 0 length
                return PrecompileResult.Failure;
            }

            int count = inputData.Length / ItemSize;
            G1 calculated = new G1();
            Span<G1> inputG1 = new G1[count];
            Span<Fr> inputFr = new Fr[count];

            for (int i = 0; i < count; i++)
            {
                Span<byte> currentBytes = inputData.Slice(i * ItemSize, ItemSize);
                if (!currentBytes.TryReadEthG1(0, out inputG1[i]) ||
                    !currentBytes.TryReadEthFr(2 * BlsExtensions.LenFp, out inputFr[i]))
                {
                    return PrecompileResult.Failure;
                }
            }
            
            G1.MultiMul(ref calculated, inputG1, inputFr);
            return new PrecompileResult(calculated.SerializeEthG1(), true);
        }
    }
}