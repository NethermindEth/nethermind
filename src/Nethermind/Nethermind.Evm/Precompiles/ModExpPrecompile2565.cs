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
using Nethermind.Int256;

namespace Nethermind.Evm.Precompiles
{
    /// <summary>
    ///     https://github.com/ethereum/EIPs/blob/vbuterin-patch-2/EIPS/bigint_modexp.md
    /// </summary>
    public class ModExpPrecompile2565 : IPrecompile
    {
        public static IPrecompile Instance = new ModExpPrecompile2565();

        private ModExpPrecompile2565()
        {
        }

        public Address Address { get; } = Address.FromNumber(5);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return 0L;
        }

        /// <summary>
        /// def calculate_gas_cost(base_length, modulus_length, exponent_length, exponent):
        /// multiplication_complexity = calculate_multiplication_complexity(base_length, modulus_length)
        /// iteration_count = calculate_iteration_count(exponent_length, exponent)
        /// return max(200, math.floor(multiplication_complexity * iteration_count / 3))
        /// </summary>
        /// <param name="inputData"></param>
        /// <param name="releaseSpec"></param>
        /// <returns></returns>
        public long DataGasCost(byte[] inputData, IReleaseSpec releaseSpec)
        {
            try
            {
                byte[] extendedInput = new byte[96];
                inputData.Slice(0, Math.Min(96, inputData.Length))
                    .CopyTo(extendedInput.AsSpan().Slice(0, Math.Min(96, inputData.Length)));

                UInt256 baseLength = new(extendedInput.AsSpan().Slice(0, 32), true);
                UInt256 expLength = new(extendedInput.AsSpan().Slice(32, 32), true);
                UInt256 modulusLength = new(extendedInput.AsSpan().Slice(64, 32), true);

                UInt256 complexity = MultComplexity(baseLength, modulusLength);

                UInt256 expLengthUpTo32 = UInt256.Min(32, expLength);
                UInt256 startIndex = 96 + baseLength; //+ expLength - expLengthUpTo32; // Geth takes head here, why?
                UInt256 exp = new(
                    inputData.SliceWithZeroPaddingEmptyOnError((BigInteger)startIndex, (int)expLengthUpTo32), true);
                UInt256 iterationCount = CalculateIterationCount(expLength, exp);

                return Math.Max(200L, (long)(complexity * iterationCount / 3));
            }
            catch (OverflowException)
            {
                return long.MaxValue;
            }
        }

        public (byte[], bool) Run(byte[] inputData, IReleaseSpec releaseSpec)
        {
            Metrics.ModExpPrecompile++;

            Span<byte> extendedInput = stackalloc byte[96];
            inputData.Slice(0, Math.Min(96, inputData.Length))
                .CopyTo(extendedInput.Slice(0, Math.Min(96, inputData.Length)));

            int baseLength = (int)new UInt256(extendedInput.Slice(0, 32), true);
            int expLength = (int)new UInt256(extendedInput.Slice(32, 32), true);
            int modulusLength = (int)new UInt256(extendedInput.Slice(64, 32), true);

            BigInteger modulusInt = inputData
                .SliceWithZeroPaddingEmptyOnError(96 + baseLength + expLength, modulusLength).ToUnsignedBigInteger();

            if (modulusInt.IsZero)
            {
                return (new byte[modulusLength], true);
            }

            BigInteger baseInt = inputData.SliceWithZeroPaddingEmptyOnError(96, baseLength).ToUnsignedBigInteger();
            BigInteger expInt = inputData.SliceWithZeroPaddingEmptyOnError(96 + baseLength, expLength)
                .ToUnsignedBigInteger();
            return (BigInteger.ModPow(baseInt, expInt, modulusInt).ToBigEndianByteArray(modulusLength), true);
        }

        /// <summary>
        /// def calculate_multiplication_complexity(base_length, modulus_length):
        /// max_length = max(base_length, modulus_length)
        /// words = math.ceil(max_length / 8)
        /// return words**2
        /// </summary>
        /// <returns></returns>
        private static UInt256 MultComplexity(UInt256 baseLength, UInt256 modulusLength)
        {
            UInt256 maxLength = UInt256.Max(baseLength, modulusLength);
            UInt256.Mod(maxLength, 8, out UInt256 mod8);
            UInt256 words = (maxLength / 8) + ((mod8.IsZero) ? UInt256.Zero : UInt256.One);
            return words * words;
        }

        /// <summary>
        /// def calculate_iteration_count(exponent_length, exponent):
        /// iteration_count = 0
        /// if exponent_length <= 32 and exponent == 0: iteration_count = 0
        /// elif exponent_length <= 32: iteration_count = exponent.bit_length() - 1
        /// elif exponent_length > 32: iteration_count = (8 * (exponent_length - 32)) + ((exponent & (2**256 - 1)).bit_length() - 1)
        /// return max(iteration_count, 1) 
        /// </summary>
        /// <param name="exponentLength"></param>
        /// <param name="exponent"></param>
        /// <returns></returns>
        private static UInt256 CalculateIterationCount(UInt256 exponentLength, UInt256 exponent)
        {
            try
            {
                UInt256 iterationCount = UInt256.Zero;
                if (exponentLength <= 32)
                {
                    if (exponent != 0)
                    {
                        iterationCount = (UInt256)(exponent.BitLen - 1);
                    }
                    else
                    {
                        iterationCount = 0;
                    }
                }
                else
                {
                    int bitLength = (exponent & UInt256.MaxValue).BitLen;
                    if (bitLength > 0)
                    {
                        bitLength--;
                    }
                        
                    iterationCount = 8 * (exponentLength - 32) + (UInt256)bitLength;
                }

                return UInt256.Max(iterationCount, UInt256.One);
            }
            catch (OverflowException e)

            {
                return UInt256.MaxValue;
            }
        }
    }
}
