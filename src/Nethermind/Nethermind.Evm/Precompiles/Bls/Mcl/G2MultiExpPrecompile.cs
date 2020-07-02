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
    public class G2MultiExpPrecompile : IPrecompile
    {
        public static IPrecompile Instance = new G2MultiExpPrecompile();

        private G2MultiExpPrecompile()
        {
        }

        public Address Address { get; } = Address.FromNumber(15);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return 0L;
        }

        public long DataGasCost(byte[] inputData, IReleaseSpec releaseSpec)
        {
            int k = inputData.Length / 288;
            return 55000L * k * Discount.For(k) / 1000;;
        }
        
        private const int ItemSize = 288;

        public (byte[], bool) Run(byte[] inputData)
        {
            inputData ??= Bytes.Empty;
            if (inputData.Length % ItemSize > 0)
            {
                // note that it will not happen in case of null / 0 length
                return (Bytes.Empty, false);
            }

            int count = inputData.Length / ItemSize;
            G2 calculated = new G2();
            Span<G2> inputG2 = new G2[count];
            Span<Fr> inputFr = new Fr[count];

            for (int i = 0; i < count; i++)
            {
                Span<byte> currentBytes = inputData.AsSpan().Slice(i * ItemSize, ItemSize);
                if (!currentBytes.TryReadEthG2(0, out inputG2[i]) ||
                    !currentBytes.TryReadEthFr(4 * BlsExtensions.LenFp, out inputFr[i]))
                {
                    return (Bytes.Empty, false);
                }
            }
            
            G2.MultiMul(ref calculated, inputG2, inputFr);
            return (BlsExtensions.SerializeEthG2(calculated), true);
        }
    }
}