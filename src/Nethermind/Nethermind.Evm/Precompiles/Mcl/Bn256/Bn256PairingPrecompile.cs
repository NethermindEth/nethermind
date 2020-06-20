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
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Precompiles.Mcl.Bn256
{
    /// <summary>
    /// https://github.com/herumi/mcl/blob/master/api.md
    /// </summary>
    public class Bn256PairingPrecompile : IPrecompile
    {
        private const int PairSize = 192;

        public static IPrecompile Instance = new Bn256PairingPrecompile();

        public Address Address { get; } = Address.FromNumber(8);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return releaseSpec.IsEip1108Enabled ? 45000L : 100000L;
        }

        public long DataGasCost(byte[] inputData, IReleaseSpec releaseSpec)
        {
            if (inputData == null)
            {
                return 0L;
            }

            return (releaseSpec.IsEip1108Enabled ? 34000L : 80000L) * (inputData.Length / PairSize);
        }

        public (byte[], bool) Run(byte[] inputData)
        {
            Metrics.Bn128PairingPrecompile++;

            inputData ??= Bytes.Empty;
            if (inputData.Length % PairSize > 0)
            {
                // note that it will not happens in case of null / 0 length
                return (Bytes.Empty, false);
            }

            UInt256 result = UInt256.One;
            if (inputData.Length > 0)
            {
                 List<(Crypto.Bn256.G1 P, Crypto.Bn256.G2 Q)> pairs = new List<(Crypto.Bn256.G1 P, Crypto.Bn256.G2 Q)>();
                 
                 // iterating over all pairs
                 for (int offset = 0; offset < inputData.Length; offset += PairSize)
                 {
                     Span<byte> pairData = inputData.Slice(offset, PairSize);
                     (Crypto.Bn256.G1 P, Crypto.Bn256.G2 Q)? pair = DecodePair(pairData);
                     if (pair == null || !pair.Value.P.IsValid() || !pair.Value.Q.IsValid())
                     {
                         return (Bytes.Empty, false);
                     }

                     pairs.Add(pair.Value);
                 }
                 
                 result = RunPairingCheck(pairs);
            }

            byte[] resultBytes = new byte[32];
            result.ToBigEndian(resultBytes);
            return (resultBytes, true);
        }

        private static UInt256 RunPairingCheck(List<(Crypto.Bn256.G1 P, Crypto.Bn256.G2 Q)> _pairs)
        {
            Crypto.Bn256.GT gt = new Crypto.Bn256.GT();
            for (int i = 0; i < _pairs.Count; i++)
            {
                (Crypto.Bn256.G1 P, Crypto.Bn256.G2 Q) pair = _pairs[i];
                if (i == 0)
                {
                    gt.MillerLoop(pair.P, pair.Q);
                }
                else
                {
                    Crypto.Bn256.GT millerLoopRes = new Crypto.Bn256.GT();
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

        private (Crypto.Bn256.G1, Crypto.Bn256.G2)? DecodePair(Span<byte> input)
        {
            (Crypto.Bn256.G1, Crypto.Bn256.G2)? res;
            Span<byte> x = input.Slice(0, 32);
            Span<byte> y = input.Slice(32, 32);

            Crypto.Bn256.G1? p = Crypto.Bn256.G1.CreateFromBigEndian(x, y);
            if (p is null)
            {
                res = null;
            }
            else
            {
                // (b, a)
                Span<byte> b = input.Slice(64, 32);
                Span<byte> a = input.Slice(96, 32);

                // (d, c)
                Span<byte> d = input.Slice(128, 32);
                Span<byte> c = input.Slice(160, 32);

                Crypto.Bn256.G2 q = Crypto.Bn256.G2.CreateFromBigEndian(a, b, c, d);

                res =  (p.Value, q);    
            }

            return res;
        }
    }
}