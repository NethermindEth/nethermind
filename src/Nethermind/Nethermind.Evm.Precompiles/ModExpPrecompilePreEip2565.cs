// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm.Precompiles;

/// <summary>
///     https://github.com/ethereum/EIPs/blob/vbuterin-patch-2/EIPS/bigint_modexp.md
/// </summary>
[Obsolete("Pre-eip2565 implementation")]
public class ModExpPrecompilePreEip2565 : IPrecompile<ModExpPrecompilePreEip2565>
{
    public static ModExpPrecompilePreEip2565 Instance = new();
    private static readonly UInt256 Eight = 8;

    private ModExpPrecompilePreEip2565()
    {
    }

    public static Address Address { get; } = Address.FromNumber(5);

    public static string Name => "MODEXP";

    public long BaseGasCost(IReleaseSpec releaseSpec) => 0L;

    public Result<long> DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        try
        {
            return inputData.Length >= 96
                ? DataGasCostInternal(inputData.Span.Slice(0, 96), inputData, releaseSpec)
                : DataGasCostInternal(inputData, releaseSpec);
        }
        catch (OverflowException)
        {
            return Errors.Overflow;
        }
    }

    private static long DataGasCostInternal(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Span<byte> extendedInput = stackalloc byte[96];
        inputData[..Math.Min(96, inputData.Length)].Span
            .CopyTo(extendedInput[..Math.Min(96, inputData.Length)]);

        return DataGasCostInternal(extendedInput, inputData, releaseSpec);
    }

    private static long DataGasCostInternal(ReadOnlySpan<byte> extendedInput, ReadOnlyMemory<byte> inputData,
        IReleaseSpec releaseSpec)
    {
        UInt256 baseLength = new(extendedInput[..32], true);
        UInt256 expLength = new(extendedInput.Slice(32, 32), true);
        UInt256 modulusLength = new(extendedInput.Slice(64, 32), true);

        UInt256 complexity = MultComplexity(UInt256.Max(baseLength, modulusLength));

        ReadOnlySpan<byte> expSignificantBytes =
            inputData.Span.SliceWithZeroPaddingEmptyOnError(96 + (int)baseLength, (int)UInt256.Min(expLength, 32));

        UInt256 lengthOver32 = expLength <= 32 ? 0 : expLength - 32;
        UInt256 adjusted = AdjustedExponentLength(lengthOver32, expSignificantBytes);
        UInt256 gas = complexity * UInt256.Max(adjusted, UInt256.One) / 20;
        return gas > long.MaxValue ? long.MaxValue : (long)gas;
    }

    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.ModExpPrecompile++;

        int baseLength = (int)inputData.Span.SliceWithZeroPaddingEmptyOnError(0, 32).ToUnsignedBigInteger();
        BigInteger expLengthBig = inputData.Span.SliceWithZeroPaddingEmptyOnError(32, 32).ToUnsignedBigInteger();
        int expLength = expLengthBig > int.MaxValue ? int.MaxValue : (int)expLengthBig;
        int modulusLength = (int)inputData.Span.SliceWithZeroPaddingEmptyOnError(64, 32).ToUnsignedBigInteger();

        BigInteger modulusInt = inputData.Span
            .SliceWithZeroPaddingEmptyOnError(96 + baseLength + expLength, modulusLength).ToUnsignedBigInteger();

        if (modulusInt.IsZero)
        {
            return new byte[modulusLength];
        }

        BigInteger baseInt = inputData.Span.SliceWithZeroPaddingEmptyOnError(96, baseLength).ToUnsignedBigInteger();
        BigInteger expInt = inputData.Span.SliceWithZeroPaddingEmptyOnError(96 + baseLength, expLength)
            .ToUnsignedBigInteger();
        return BigInteger.ModPow(baseInt, expInt, modulusInt).ToBigEndianByteArray(modulusLength);
    }

    private static UInt256 MultComplexity(in UInt256 adjustedExponentLength)
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

    private static UInt256 AdjustedExponentLength(in UInt256 lengthOver32, ReadOnlySpan<byte> exponent)
    {
        bool overflow = false;
        bool underflow = false;
        UInt256 result;

        int leadingZeros = exponent.LeadingZerosCount();
        if (leadingZeros == exponent.Length)
        {
            overflow |= UInt256.MultiplyOverflow(lengthOver32, Eight, out result);
            return overflow ? UInt256.MaxValue : result;
        }

        overflow |= UInt256.AddOverflow(lengthOver32, (UInt256)exponent.Length, out result);
        underflow |= UInt256.SubtractUnderflow(result, (UInt256)leadingZeros, out result);
        underflow |= UInt256.SubtractUnderflow(result, UInt256.One, out result);
        overflow |= UInt256.MultiplyOverflow(result, Eight, out result);
        overflow |= UInt256.AddOverflow(result, (UInt256)exponent[leadingZeros].GetHighestSetBitIndex(), out result);
        underflow |= UInt256.SubtractUnderflow(result, UInt256.One, out result);

        return overflow ? UInt256.MaxValue : underflow ? UInt256.MinValue : result;
    }
}
