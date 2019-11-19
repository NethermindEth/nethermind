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
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles
{
    public class Blake2BPrecompiledContract : IPrecompiledContract
    {
        private const int RequiredInputLength = 213;
        public static readonly IPrecompiledContract Instance = new Blake2BPrecompiledContract();

        public Address Address { get; } = Address.FromNumber(9);

        public long BaseGasCost(IReleaseSpec releaseSpec) => 0;

        public long DataGasCost(byte[] inputData, IReleaseSpec releaseSpec)
        {
            if (inputData.Length != RequiredInputLength)
            {
                return 0;
            }
            
            var finalByte = inputData[212];
            if (finalByte != 0 && finalByte != 1)
            {
                return 0;
            }
            
            var rounds = inputData.AsSpan().Slice(0, 4).ReadEthUInt32();

            return rounds;
        }

        public (byte[], bool) Run(byte[] inputData)
        {
            if (inputData.Length != RequiredInputLength)
            {
                return (Bytes.Empty, false);
            }

            var finalByte = inputData[212];
            if (finalByte != 0 && finalByte != 1)
            {
                return (Bytes.Empty, false);
            }
            
            var blake = new Blake2Compression();
            var result = new byte[64];
            blake.Compress(inputData, result);

            return (result, true);
        }
    }
}