//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Core.Specs;
using Nethermind.Crypto.Bls;

namespace Nethermind.Evm.Precompiles.Snarks.Shamatar
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

        public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            return (releaseSpec.IsEip1108Enabled ? 34000L : 80000L) * (inputData.Length / PairSize);
        }

        public (ReadOnlyMemory<byte>, bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            Metrics.Bn256PairingPrecompile++;

            (byte[], bool) result;
            if (inputData.Length % PairSize > 0)
            {
                // note that it will not happen in case of null / 0 length
                result = (Array.Empty<byte>(), false);
            }
            else
            {
                /* we modify input in place here and this is save for EVM but not
                   safe in benchmarks so we need to remember to clone */
                Span<byte> output = stackalloc byte[64];
                Span<byte> inputDataSpan = inputData.ToArray().AsSpan();
                Span<byte> inputReshuffled = stackalloc byte[PairSize];
                for (int i = 0; i < inputData.Length / PairSize; i++)
                {
                    inputDataSpan.Slice(i * PairSize + 0, 64).CopyTo(inputReshuffled.Slice(0, 64));
                    inputDataSpan.Slice(i * PairSize + 64, 32).CopyTo(inputReshuffled.Slice(96, 32));
                    inputDataSpan.Slice(i * PairSize + 96, 32).CopyTo(inputReshuffled.Slice(64, 32));
                    inputDataSpan.Slice(i * PairSize + 128, 32).CopyTo(inputReshuffled.Slice(160, 32));
                    inputDataSpan.Slice(i * PairSize + 160, 32).CopyTo(inputReshuffled.Slice(128, 32));
                    inputReshuffled.CopyTo(inputDataSpan.Slice(i * PairSize, PairSize));
                }
                
                bool success = ShamatarLib.Bn256Pairing(inputDataSpan, output);

                if (success)
                {
                    result = (output.Slice(0, 32).ToArray(), true);
                }
                else
                {
                    result = (Array.Empty<byte>(), false);
                }
            }

            return result;
        }
    }
}
