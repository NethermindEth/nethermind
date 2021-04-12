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
    [Obsolete("Pre-eip2565 implementation")]
    public class ModExpPrecompilePreEip2565 : IPrecompile
    {
        public static IPrecompile Instance = new ModExpPrecompilePreEip2565();

        private ModExpPrecompilePreEip2565()
        {
        }

        public Address Address { get; } = Address.FromNumber(5);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return 0L;
        }
        
        public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            try
            {
                Span<byte> extendedInput = stackalloc byte[96];
                inputData.Slice(0, Math.Min(96, inputData.Length)).Span
                    .CopyTo(extendedInput.Slice(0, Math.Min(96, inputData.Length)));
                
                UInt256 baseLength = new(extendedInput.Slice(0, 32), true);
                UInt256 expLength = new(extendedInput.Slice(32, 32), true);
                UInt256 modulusLength = new(extendedInput.Slice(64, 32), true);

                UInt256 complexity = MultComplexity(UInt256.Max(baseLength, modulusLength));

                byte[] expSignificantBytes = inputData.Span.SliceWithZeroPaddingEmptyOnError(96 + (int)baseLength, (int)UInt256.Min(expLength, 32));

                UInt256 lengthOver32 = expLength <= 32 ? 0 : expLength - 32;
                UInt256 adjusted = AdjustedExponentLength(lengthOver32, expSignificantBytes);
                UInt256 gas = complexity * UInt256.Max(adjusted, UInt256.One) / 20;
                return gas > long.MaxValue ? long.MaxValue : (long)gas;
            }
            catch (OverflowException)
            {
                return long.MaxValue;
            }
        }

        public (ReadOnlyMemory<byte>, bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            Metrics.ModExpPrecompile++;
            
            int baseLength = (int)inputData.Span.SliceWithZeroPaddingEmptyOnError(0, 32).ToUnsignedBigInteger();
            BigInteger expLengthBig = inputData.Span.SliceWithZeroPaddingEmptyOnError(32, 32).ToUnsignedBigInteger();
            int expLength = expLengthBig > int.MaxValue ? int.MaxValue : (int)expLengthBig;
            int modulusLength = (int)inputData.Span.SliceWithZeroPaddingEmptyOnError(64, 32).ToUnsignedBigInteger();
            
            BigInteger modulusInt = inputData.Span.SliceWithZeroPaddingEmptyOnError(96 + baseLength + expLength, modulusLength).ToUnsignedBigInteger();

            if (modulusInt.IsZero)
            {
                return (new byte[modulusLength], true);
            }

            BigInteger baseInt = inputData.Span.SliceWithZeroPaddingEmptyOnError(96, baseLength).ToUnsignedBigInteger();
            BigInteger expInt = inputData.Span.SliceWithZeroPaddingEmptyOnError(96 + baseLength, expLength).ToUnsignedBigInteger();
            return (BigInteger.ModPow(baseInt, expInt, modulusInt).ToBigEndianByteArray(modulusLength), true);
        }

        private UInt256 MultComplexity(UInt256 adjustedExponentLength)
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

        private static UInt256 AdjustedExponentLength(UInt256 lengthOver32, byte[] exponent)
        {
            int leadingZeros = exponent.AsSpan().LeadingZerosCount();
            if (leadingZeros == exponent.Length)
            {
                return lengthOver32 * 8;
            }

            return
                (
                    lengthOver32 
                    + (UInt256)exponent.Length 
                    - (UInt256)leadingZeros 
                    - (UInt256)1) 
                * 8 
                + (UInt256)(exponent[leadingZeros].GetHighestSetBitIndex() - 1);
        }
    }
}
