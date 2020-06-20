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
using Nethermind.Crypto;
using Nethermind.Crypto.Bls;

namespace Nethermind.Evm.Precompiles.Mcl.Bls
{
    /// <summary>
    /// https://eips.ethereum.org/EIPS/eip-2537
    /// </summary>
    public class G1AddPrecompile : IPrecompile
    {
        public static IPrecompile Instance = new G1AddPrecompile();

        private G1AddPrecompile()
        {
        }

        public Address Address { get; } = Address.FromNumber(10);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return 600L;
        }

        public long DataGasCost(byte[] inputData, IReleaseSpec releaseSpec)
        {
            return 0L;
        }

        public (byte[], bool) Run(byte[] inputData)
        {
            Span<byte> inputDataSpan = stackalloc byte[4 * Common.LenFp];
            Mcl.PrepareInputData(inputData, inputDataSpan);

            // for diagnostic - can be removed
            // Common.TryReadFp(inputDataSpan, 0 * Common.LenFp, out MclBls12.Fp fp1);
            // Common.TryReadFp(inputDataSpan, 1 * Common.LenFp, out MclBls12.Fp fp2);
            // Common.TryReadFp(inputDataSpan, 2 * Common.LenFp, out MclBls12.Fp fp3);
            // Common.TryReadFp(inputDataSpan, 3 * Common.LenFp, out MclBls12.Fp fp4);
            
            (byte[], bool) result;
            if (Common.TryReadEthG1(inputDataSpan, 0 * Common.LenFp, out G1 a) &&
                Common.TryReadEthG1(inputDataSpan, 2 * Common.LenFp, out G1 b))
            {
                a.Add(a, b);
                result = (Common.SerializeEthG1(a), true);
            }
            else
            {
                result = (Bytes.Empty, false);
            }
            
            return result;
        }
    }
}