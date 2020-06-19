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
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Precompiles.Bn256
{
    /// <summary>
    /// https://github.com/herumi/mcl/blob/master/api.md
    /// </summary>
    public class Bn256AddPrecompile : IPrecompile
    {
        public static IPrecompile Instance = new Bn256AddPrecompile();

        private static byte[] _zeroResult = new byte[64];

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

            inputData ??= Bytes.Empty;
            Span<byte> inputDataSpan = stackalloc byte[128];
            inputData.AsSpan(0, Math.Min(128, inputData.Length)).CopyTo(inputDataSpan.Slice(0, Math.Min(128, inputData.Length)));

            UInt256.CreateFromBigEndian(out UInt256 x1, inputDataSpan.Slice(0, 32));
            UInt256.CreateFromBigEndian(out UInt256 y1, inputDataSpan.Slice(32, 32));
            UInt256.CreateFromBigEndian(out UInt256 x2, inputDataSpan.Slice(64, 32));
            UInt256.CreateFromBigEndian(out UInt256 y2, inputDataSpan.Slice(96, 32));

            Crypto.Bn256.G1 a = Crypto.Bn256.G1.Create(x1, y1);
            if (!a.IsValid())
            {
                return (Bytes.Empty, false);
            }

            Crypto.Bn256.G1 b = Crypto.Bn256.G1.Create(x2, y2);
            if (!b.IsValid())
            {
                return (Bytes.Empty, false);
            }

            Crypto.Bn256.G1 result = new Crypto.Bn256.G1();
            result.Add(a, b);

            byte[] encodedResult;
            if (result.IsZero())
            {
                encodedResult = _zeroResult;
            }
            else
            {
                // encodedResult = new byte[64];
                // result.Serialize(encodedResult.AsSpan(0, 32), 32);
                string[] resultStrings = result.GetStr(0).Split(" ");
                UInt256 resA = UInt256.Parse(resultStrings[1]);
                UInt256 resB = UInt256.Parse(resultStrings[2]);
                encodedResult = EncodeResult(resA, resB);
            }

            return (encodedResult, true);
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