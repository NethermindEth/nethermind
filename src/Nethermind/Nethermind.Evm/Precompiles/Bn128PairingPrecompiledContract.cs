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

using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto.ZkSnarks;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Precompiles
{
    /// <summary>
    ///     Code adapted from ethereumJ (https://github.com/ethereum/ethereumj)
    /// </summary>
    public class Bn128PairingPrecompiledContract : IPrecompiledContract
    {
        private const int PairSize = 192;

        public static IPrecompiledContract Instance = new Bn128PairingPrecompiledContract();

        private Bn128PairingPrecompiledContract()
        {
        }

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

            return (releaseSpec.IsEip1108Enabled ? 34000L : 80000L)* (inputData.Length / PairSize);
        }

        public (byte[], bool) Run(byte[] inputData)
        {
            Metrics.Bn128PairingPrecompile++;
            
            if (inputData == null)
            {
                inputData = Bytes.Empty;
            }

            // fail if input len is not a multiple of PAIR_SIZE
            if (inputData.Length % PairSize > 0)
            {
                return (Bytes.Empty, false);
            }

            PairingCheck check = PairingCheck.Create();

            // iterating over all pairs
            for (int offset = 0; offset < inputData.Length; offset += PairSize)
            {
                (Bn128Fp, Bn128Fp2) pair = DecodePair(inputData, offset);

                // fail if decoding has failed
                if (pair.Item1 == null || pair.Item2 == null)
                {
                    return (Bytes.Empty, false);
                }

                check.AddPair(pair.Item1, pair.Item2);
            }

            check.Run();
            UInt256 result = check.Result();
            byte[] resultBytes = new byte[32];
            result.ToBigEndian(resultBytes);
            return (resultBytes, true);
        }

        private (Bn128Fp, Bn128Fp2) DecodePair(byte[] input, int offset)
        {
            byte[] x = input.Slice(offset + 0, 32);
            byte[] y = input.Slice(offset + 32, 32);

            Bn128Fp p1 = Bn128Fp.CreateInG1(x, y);

            // fail if point is invalid
            if (p1 == null)
            {
                return (null, null);
            }

            // (b, a)
            byte[] b = input.Slice(offset + 64, 32);
            byte[] a = input.Slice(offset + 96, 32);

            // (d, c)
            byte[] d = input.Slice(offset + 128, 32);
            byte[] c = input.Slice(offset + 160, 32);

            Bn128Fp2 p2 = Bn128Fp2.CreateInG2(a, b, c, d);

            // fail if point is invalid
            if (p2 == null)
            {
                return (null, null);
            }

            return (p1, p2);
        }
    }
}