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
using Nethermind.Crypto.Bn256;
using Nethermind.Crypto.ZkSnarks;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Precompiles
{
    public class Bn256MulPrecompile : IPrecompile
    {
        public static IPrecompile Instance = new Bn256MulPrecompile();

        private Bn256MulPrecompile()
        {
            Bn256.init();
        }

        public Address Address { get; } = Address.FromNumber(6);

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
            
            inputData ??= Bytes.Empty;
            Span<byte> inputDataSpan = stackalloc byte[96];
            inputData.AsSpan().CopyTo(inputDataSpan.Slice(0, inputData.Length));

            UInt256.CreateFromBigEndian(out UInt256 x, inputDataSpan.Slice(0, 32));
            UInt256.CreateFromBigEndian(out UInt256 y, inputDataSpan.Slice(32, 32));
            UInt256.CreateFromBigEndian(out UInt256 s, inputDataSpan.Slice(64, 32));

            Bn256.G1 a = new Bn256.G1();
            if (y.IsEven)
            {
                a.setStr($"2 {x.ToString()}", 0);
            }
            else
            {
                a.setStr($"3 {x.ToString()}", 0);
            }

            BigInteger sBigInt = inputDataSpan.Slice(64, 32).ToUnsignedBigInteger();
            Bn256.G1 resultAlt = MulAlternative(ref a, sBigInt);

            string[] resultStrings = resultAlt.GetStr(0).Split(" ");
            UInt256 resA = UInt256.Parse(resultStrings[1]);
            UInt256 resB = UInt256.Parse(resultStrings[2]);
            
            return (EncodeResult(resA, resB), true);
        }

        private static Bn256.G1 Mul(ref Bn256.G1 g1, BigInteger s)
        {
            // this one is returning different values - probably SetStr on Fp is wrong here
            
            Fp fp = new Fp(s);
            Bn256.Fr b = new Bn256.Fr();
            b.SetStr($"{fp.ToString()}", 0);

            Bn256.G1 res = new Bn256.G1();
            res.Mul(g1, b);
            return res;
        }
        
        private static Bn256.G1 MulAlternative(ref Bn256.G1 g1, BigInteger s)
        {
            if (s.IsZero) // P * 0 = 0
            {
                g1.Clear();
            }

            if (g1.IsZero())
            {
                return g1;
            }

            Bn256.G1 res = new Bn256.G1();
            res.Clear();

            int bitLength = s.BitLength();
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
        
        private static byte[] EncodeResult(UInt256 w1, UInt256 w2)
        {
            byte[] result = new byte[64];
            w1.ToBigEndian(result.AsSpan(0, 32));
            w2.ToBigEndian(result.AsSpan(32, 32));
            return result;
        }
    }
}