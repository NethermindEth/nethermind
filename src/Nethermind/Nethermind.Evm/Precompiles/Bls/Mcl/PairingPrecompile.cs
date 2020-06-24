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

namespace Nethermind.Evm.Precompiles.Bls.Mcl
{
    /// <summary>
    /// https://eips.ethereum.org/EIPS/eip-2537
    /// </summary>
    public class PairingPrecompile : IPrecompile
    {
        public static IPrecompile Instance = new PairingPrecompile();

        private const int PairSize = 384;

        private PairingPrecompile()
        {
        }

        public Address Address { get; } = Address.FromNumber(16);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return 115000L;
        }

        public long DataGasCost(Span<byte> inputData, IReleaseSpec releaseSpec)
        {
            if (inputData == null)
            {
                return 0L;
            }

            return 23000L * (inputData.Length / PairSize);
        }

        public PrecompileResult Run(Span<byte> inputData)
        {
            Metrics.Bn256PairingPrecompile++;
            
            if (inputData.Length % PairSize > 0)
            {
                // note that it will not happens in case of null / 0 length
                return PrecompileResult.Failure;
            }

            UInt256 result = UInt256.One;
            if (inputData.Length > 0)
            {
                List<(G1 P, G2 Q)> pairs = new List<(G1 P, G2 Q)>();
                // iterating over all pairs
                for (int offset = 0; offset < inputData.Length; offset += PairSize)
                {
                    Span<byte> pairData = inputData.Slice(offset, PairSize);
                    (G1 P, G2 Q)? pair = DecodePair(pairData);
                    if (pair == null)
                    {
                        return PrecompileResult.Failure;
                    }

                    pairs.Add(pair.Value);
                }

                result = RunPairingCheck(pairs);
            }

            byte[] resultBytes = new byte[32];
            result.ToBigEndian(resultBytes);
            return new PrecompileResult(resultBytes, true);
        }

        private static UInt256 RunPairingCheck(List<(G1 P, G2 Q)> _pairs)
        {
            GT gt = new GT();
            for (int i = 0; i < _pairs.Count; i++)
            {
                (G1 P, G2 Q) pair = _pairs[i];
                if (i == 0)
                {
                    gt.MillerLoop(pair.P, pair.Q);
                }
                else
                {
                    GT millerLoopRes = new GT();
                    if (!millerLoopRes.IsOne())
                    {
                        millerLoopRes.MillerLoop(pair.P, pair.Q);
                    }

                    gt.Mul(gt, millerLoopRes);
                }
            }

            gt.FinalExp(gt);
            UInt256 result = gt.IsOne() ? UInt256.One : UInt256.Zero;
            return result;
        }

        private static (G1, G2)? DecodePair(Span<byte> input)
        {
            (G1, G2)? result;

            if (input.TryReadEthG1(0, out G1 p) &&
                input.TryReadEthG2(2 * BlsExtensions.LenFp, out G2 q))
            {
                result = (p, q);
            }
            else
            {
                result = null;
            }

            return result;
        }
    }
}