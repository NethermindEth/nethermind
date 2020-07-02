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
using Nethermind.Crypto.ZkSnarks.Obsolete;

namespace Nethermind.Evm.Precompiles.Snarks.EthereumJ
{
    /// <summary>
    ///     Code adapted from ethereumJ (https://github.com/ethereum/ethereumj)
    /// </summary>
    [Obsolete("Use Bn256AddPrecompile instead")]
    public class Bn256AddPrecompile : IPrecompile
    {
        public static IPrecompile Instance = new Bn256AddPrecompile();

        private Bn256AddPrecompile()
        {
        }

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
            Metrics.Bn256AddPrecompile++;
            
            if (inputData == null)
            {
                inputData = Bytes.Empty;
            }

            if (inputData.Length < 128)
            {
                inputData = inputData.PadRight(128);
            }

            Span<byte> x1 = inputData.AsSpan().Slice(0, 32);
            Span<byte> y1 = inputData.AsSpan().Slice(32, 32);

            Span<byte> x2 = inputData.AsSpan().Slice(64, 32);
            Span<byte> y2 = inputData.AsSpan().Slice(96, 32);

            Bn128Fp p1 = Bn128Fp.Create(x1, y1);
            if (p1 == null)
            {
                return (Bytes.Empty, false);
            }

            Bn128Fp p2 = Bn128Fp.Create(x2, y2);
            if (p2 == null)
            {
                return (Bytes.Empty, false);
            }

            Bn128Fp res = p1.Add(p2).ToEthNotation();

            return (EncodeResult(res.X.GetBytes(), res.Y.GetBytes()), true);
        }
        
        private static byte[] EncodeResult(byte[] w1, byte[] w2) {

            byte[] result = new byte[64];

            // TODO: do I need to strip leading zeros here? // probably not
            w1.AsSpan().WithoutLeadingZeros().CopyTo(result.AsSpan().Slice(32 - w1.Length, w1.Length));
            w2.AsSpan().WithoutLeadingZeros().CopyTo(result.AsSpan().Slice(64 - w2.Length, w2.Length));
            return result;
        }
    }
}