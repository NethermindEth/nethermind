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
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Crypto.ZkSnarks;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Precompiles.Mcl.Bn256
{
    /// <summary>
    /// https://github.com/herumi/mcl/blob/master/api.md
    /// </summary>
    public class Bn256MulPrecompile : IPrecompile
    {
        public static IPrecompile Instance = new Bn256MulPrecompile();

        public Address Address { get; } = Address.FromNumber(7);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return releaseSpec.IsEip1108Enabled ? 6000L : 40000L;
        }

        public long DataGasCost(byte[] inputData, IReleaseSpec releaseSpec)
        {
            return 0L;
        }

        public (byte[], bool) Run(byte[] inputData)
        {
            Metrics.Bn128MulPrecompile++;
            Span<byte> inputDataSpan = stackalloc byte[96];
            Mcl.PrepareInputData(inputData, inputDataSpan);

            (byte[], bool) result;
            if (Common.TryReadEthG1(inputDataSpan, 0 * Crypto.ZkSnarks.Bn256.LenFp, out G1 a))
            {
                Common.ReadFr(inputDataSpan, 2 * Crypto.ZkSnarks.Bn256.LenFp, out Fr fr);
                // UInt256 scalar = Mcl.ReadScalar(inputDataSpan, 2 * Crypto.Bn256.LenFp);

                // Crypto.Bn256.G1 result = MulAlternative(a, scalar);
                // Crypto.Bn256.G1 mulRes = Mul(a, scalar % Crypto.Bn256.R);
                G1 mulRes = Mul(a, fr);
                result = (Common.SerializeEthG1(mulRes), true);
            }
            else
            {
                result = (Bytes.Empty, false);
            }

            return result;
        }

        private static G1 Mul(G1 g1, Fr fr)
        {
            G1 res = new G1();
            res.Mul(g1, fr);
            return res;
        }

        private static G1 Mul(G1 g1, BigInteger s)
        {
            Fr b = new Fr();
            b.SetStr($"{s.ToString()}", 10);

            G1 res = new G1();
            res.Mul(g1, b);
            return res;
        }

        private static G1 MulAlternative(G1 g1, UInt256 s)
        {
            if (s.IsZero) // P * 0 = 0
            {
                g1.Clear();
            }

            if (g1.IsZero())
            {
                return g1;
            }

            G1 res = new G1();
            res.Clear();

            int bitLength = ((BigInteger) s).BitLength();
            for (int i = bitLength - 1; i >= 0; i--)
            {
                res.Dbl(res);
                if (s.TestBit(i))
                {
                    res.Add(res, g1);
                }
            }

            return res;
        }
    }
}