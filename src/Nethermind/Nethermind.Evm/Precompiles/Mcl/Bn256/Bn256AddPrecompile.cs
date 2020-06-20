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
using Nethermind.Crypto.ZkSnarks;

namespace Nethermind.Evm.Precompiles.Mcl.Bn256
{
    /// <summary>
    /// https://github.com/herumi/mcl/blob/master/api.md
    /// </summary>
    public class Bn256AddPrecompile : IPrecompile
    {
        public static IPrecompile Instance = new Bn256AddPrecompile();

        public Address Address { get; } = Address.FromNumber(6);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return releaseSpec.IsEip1108Enabled ? 150L : 500L;
        }

        public long DataGasCost(byte[] inputData, IReleaseSpec releaseSpec)
        {
            return 0L;
        }

        public (byte[], bool) Run(byte[] inputData)
        {
            Metrics.Bn128AddPrecompile++;
            Span<byte> inputDataSpan = stackalloc byte[128];
            Mcl.PrepareInputData(inputData, inputDataSpan);

            (byte[], bool) result;
            if (Common.TryReadEthG1(inputDataSpan, 0 * Crypto.ZkSnarks.Bn256.LenFp, out G1 a) &&
                Common.TryReadEthG1(inputDataSpan, 2 * Crypto.ZkSnarks.Bn256.LenFp, out G1 b))
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