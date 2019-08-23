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
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Precompiles
{
    /// <summary>
    ///     https://github.com/ethereum/EIPs/blob/vbuterin-patch-2/EIPS/bigint_modexp.md
    /// </summary>
    public class ModExpPrecompiledContract : IPrecompiledContract
    {
        public static IPrecompiledContract Instance = new ModExpPrecompiledContract();

        private ModExpPrecompiledContract()
        {
        }

        public Address Address { get; } = Address.FromNumber(5);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return 0L;
        }
        
        public long DataGasCost(byte[] inputData, IReleaseSpec releaseSpec)
        {
            try
            {
                BigInteger baseLength = inputData.SliceWithZeroPaddingEmptyOnError(0, 32).ToUnsignedBigInteger();
                BigInteger expLength = inputData.SliceWithZeroPaddingEmptyOnError(32, 32).ToUnsignedBigInteger();
                BigInteger modulusLength = inputData.SliceWithZeroPaddingEmptyOnError(64, 32).ToUnsignedBigInteger();

                BigInteger complexity = MultComplexity(BigInteger.Max(baseLength, modulusLength));

                byte[] expSignificantBytes = inputData.SliceWithZeroPaddingEmptyOnError(96 + (int)baseLength, (int)BigInteger.Min(expLength, 32));

                BigInteger lengthOver32 = expLength <= 32 ? 0 : expLength - 32;
                BigInteger adjusted = AdjustedExponentLength(lengthOver32, expSignificantBytes);
                BigInteger gas = complexity * BigInteger.Max(adjusted, BigInteger.One) / 20;
                return (long)gas;
            }
            catch (OverflowException)
            {
                return long.MaxValue;
            }
        }

        public (byte[], bool) Run(byte[] inputData)
        {
            Metrics.ModExpPrecompile++;
            
            int baseLength = (int)inputData.SliceWithZeroPaddingEmptyOnError(0, 32).ToUnsignedBigInteger();
            BigInteger expLengthBig = inputData.SliceWithZeroPaddingEmptyOnError(32, 32).ToUnsignedBigInteger();
            int expLength = expLengthBig > int.MaxValue ? int.MaxValue : (int)expLengthBig;
            int modulusLength = (int)inputData.SliceWithZeroPaddingEmptyOnError(64, 32).ToUnsignedBigInteger();

            BigInteger baseInt = inputData.SliceWithZeroPaddingEmptyOnError(96, baseLength).ToUnsignedBigInteger();
            BigInteger expInt = inputData.SliceWithZeroPaddingEmptyOnError(96 + baseLength, expLength).ToUnsignedBigInteger();
            BigInteger modulusInt = inputData.SliceWithZeroPaddingEmptyOnError(96 + baseLength + expLength, modulusLength).ToUnsignedBigInteger();

            if (modulusInt.IsZero)
            {
                return (new byte[modulusLength], true);
            }

            return (BigInteger.ModPow(baseInt, expInt, modulusInt).ToBigEndianByteArray(modulusLength), true);
        }

        private BigInteger MultComplexity(BigInteger adjustedExponentLength)
        {
            if (adjustedExponentLength <= 64)
            {
                return adjustedExponentLength * adjustedExponentLength;
            }

            if (adjustedExponentLength <= 1024)
            {
                return adjustedExponentLength * adjustedExponentLength / 4 + 96 * adjustedExponentLength - 3072;
            }

            return adjustedExponentLength * adjustedExponentLength / 16 + 480 * adjustedExponentLength - 199680;
        }

        private static BigInteger AdjustedExponentLength(BigInteger lengthOver32, byte[] exponent)
        {
            int leadingZeros = exponent.AsSpan().LeadingZerosCount();
            if (leadingZeros == exponent.Length)
            {
                return lengthOver32 * 8;
            }

            return (lengthOver32 + exponent.Length - leadingZeros - 1) * 8 + exponent[leadingZeros].GetHighestSetBitIndex();
        }
    }
}