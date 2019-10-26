/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto.ZkSnarks;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles
{
    /// <summary>
    ///     Code adapted from ethereumJ (https://github.com/ethereum/ethereumj)
    /// </summary>
    public class Bn128MulPrecompiledContract : IPrecompiledContract
    {
        public static IPrecompiledContract Instance = new Bn128MulPrecompiledContract();

        private Bn128MulPrecompiledContract()
        {
        }

        public Address Address { get; } = Address.FromNumber(7);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return releaseSpec.IsEip1108Enabled ? 6000L : 40000L;
        }

        public long DataGasCost(byte[] inputData, IReleaseSpec releaseSpec = null)
        {
            return 0L;
        }

        public (byte[], bool) Run(byte[] inputData)
        {
            Metrics.Bn128MulPrecompile++;
            
            if (inputData == null)
            {
                inputData = Bytes.Empty;
            }
            
            if (inputData.Length < 96)
            {
                inputData = inputData.PadRight(96);
            }
            
            byte[] x = inputData.Slice(0, 32);
            byte[] y = inputData.Slice(32, 32);
            
            byte[] s = inputData.Slice(64, 32);

            Bn128Fp p = Bn128Fp.Create(x, y);
            if (p == null)
            {
                return (Bytes.Empty, false);
            }

            Bn128Fp res = p.Mul(s.ToUnsignedBigInteger()).ToEthNotation();

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