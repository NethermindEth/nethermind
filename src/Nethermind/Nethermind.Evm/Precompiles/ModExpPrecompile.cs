// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.GmpBindings;
using Nethermind.Int256;

namespace Nethermind.Evm.Precompiles;

/// <summary>
///     https://github.com/ethereum/EIPs/blob/vbuterin-patch-2/EIPS/bigint_modexp.md
/// </summary>
public class ModExpPrecompile : IPrecompile<ModExpPrecompile>
{
    public static readonly ModExpPrecompile Instance = new();
    /// <summary>
    /// Maximum input size (in bytes) for the modular exponentiation operation under EIP-7823.
    /// This constant defines the upper limit for the size of the input data that can be processed.
    /// For more details, see: https://eips.ethereum.org/EIPS/eip-7823
    /// </summary>
    public const int ModExpMaxInputSizeEip7823 = 1024;

    private ModExpPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(5);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 0L;

    /// <summary>
    /// https://github.com/ethereum/EIPs/pull/2892
    /// ADJUSTED_EXPONENT_LENGTH is defined as follows.
    /// If length_of_EXPONENT &lt;= 32, and all bits in EXPONENT are 0, return 0
    /// If length_of_EXPONENT &lt;= 32, then return the index of the highest bit in EXPONENT (eg. 1 -> 0, 2 -> 1, 3 -> 1, 255 -> 7, 256 -> 8).
    /// If length_of_EXPONENT > 32, then return 8 * (length_of_EXPONENT - 32) plus the index of the highest bit in the first 32 bytes of EXPONENT (eg. if EXPONENT = \x00\x00\x01\x00.....\x00, with one hundred bytes, then the result is 8 * (100 - 32) + 253 = 797). If all of the first 32 bytes of EXPONENT are zero, return exactly 8 * (length_of_EXPONENT - 32).
    /// </summary>
    /// <param name="inputData"></param>
    /// <param name="releaseSpec"></param>
    /// <returns>Gas cost of the MODEXP operation in the context of EIP2565</returns>
    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (!releaseSpec.IsEip2565Enabled)
        {
#pragma warning disable 618
            return ModExpPrecompilePreEip2565.Instance.DataGasCost(inputData, releaseSpec);
#pragma warning restore 618
        }

        try
        {
            return inputData.Length >= 96
                ? DataGasCostInternal(inputData.Span.Slice(0, 96), inputData, releaseSpec)
                : DataGasCostInternal(inputData, releaseSpec);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static long DataGasCostInternal(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Span<byte> extendedInput = stackalloc byte[96];
        inputData[..Math.Min(96, inputData.Length)].Span
            .CopyTo(extendedInput[..Math.Min(96, inputData.Length)]);

        return DataGasCostInternal(extendedInput, inputData, releaseSpec);
    }

    private static long DataGasCostInternal(ReadOnlySpan<byte> extendedInput, ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        UInt256 baseLength = new(extendedInput[..32], true);
        UInt256 expLength = new(extendedInput.Slice(32, 32), true);
        UInt256 modulusLength = new(extendedInput.Slice(64, 32), true);

        if (ExceedsMaxInputSize(releaseSpec, baseLength, expLength, modulusLength))
        {
            return long.MaxValue;
        }

        UInt256 complexity = MultComplexity(baseLength, modulusLength, releaseSpec.IsEip7883Enabled);

        UInt256 expLengthUpTo32 = UInt256.Min(32, expLength);
        UInt256 startIndex = 96 + baseLength; //+ expLength - expLengthUpTo32; // Geth takes head here, why?
        UInt256 exp = new(inputData.Span.SliceWithZeroPaddingEmptyOnError((int)startIndex, (int)expLengthUpTo32), true);
        UInt256 iterationCount = CalculateIterationCount(expLength, exp, releaseSpec.IsEip7883Enabled);
        bool overflow = UInt256.MultiplyOverflow(complexity, iterationCount, out UInt256 result);
        result /= 3;
        return result > long.MaxValue || overflow
            ? long.MaxValue
            : Math.Max(releaseSpec.IsEip7883Enabled ? GasCostOf.MinModExpEip7883 : GasCostOf.MinModExpEip2565, (long)result);
    }

    private static bool ExceedsMaxInputSize(IReleaseSpec releaseSpec, UInt256 baseLength, UInt256 expLength, UInt256 modulusLength)
        => releaseSpec.IsEip7823Enabled &&
            (baseLength > ModExpMaxInputSizeEip7823 || expLength > ModExpMaxInputSizeEip7823 || modulusLength > ModExpMaxInputSizeEip7823);

    private static (int, int, int) GetInputLengths(ReadOnlyMemory<byte> inputData)
    {
        Span<byte> extendedInput = stackalloc byte[96];
        inputData[..Math.Min(96, inputData.Length)].Span
            .CopyTo(extendedInput[..Math.Min(96, inputData.Length)]);

        int baseLength = (int)new UInt256(extendedInput[..32], true);
        UInt256 expLengthUint256 = new(extendedInput.Slice(32, 32), true);
        int expLength = expLengthUint256 > Array.MaxLength ? Array.MaxLength : (int)expLengthUint256;
        int modulusLength = (int)new UInt256(extendedInput.Slice(64, 32), true);

        return (baseLength, expLength, modulusLength);
    }

    public unsafe (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.ModExpPrecompile++;

        (int baseLength, int expLength, int modulusLength) = GetInputLengths(inputData);

        if (ExceedsMaxInputSize(releaseSpec, (uint)baseLength, (uint)expLength, (uint)modulusLength))
            return IPrecompile.Failure;

        // if both are 0, then expLength can be huge, which leads to a potential buffer too big exception
        if (baseLength == 0 && modulusLength == 0)
            return (Bytes.Empty, true);

        using var modulusInt = mpz_t.Create();

        fixed (byte* modulusData = inputData.Span.SliceWithZeroPaddingEmptyOnError(96 + baseLength + expLength, modulusLength))
        {
            if (modulusData is not null)
                Gmp.mpz_import(modulusInt, (nuint)modulusLength, 1, 1, 1, nuint.Zero, (nint)modulusData);
        }

        if (Gmp.mpz_sgn(modulusInt) == 0)
            return (new byte[modulusLength], true);

        using var baseInt = mpz_t.Create();
        using var expInt = mpz_t.Create();
        using var powmResult = mpz_t.Create();

        fixed (byte* baseData = inputData.Span.SliceWithZeroPaddingEmptyOnError(96, baseLength))
        fixed (byte* expData = inputData.Span.SliceWithZeroPaddingEmptyOnError(96 + baseLength, expLength))
        {
            if (baseData is not null)
                Gmp.mpz_import(baseInt, (nuint)baseLength, 1, 1, 1, nuint.Zero, (nint)baseData);

            if (expData is not null)
                Gmp.mpz_import(expInt, (nuint)expLength, 1, 1, 1, nuint.Zero, (nint)expData);
        }

        Gmp.mpz_powm(powmResult, baseInt, expInt, modulusInt);

        var powmResultLen = (int)(Gmp.mpz_sizeinbase(powmResult, 2) + 7) / 8;
        var offset = modulusLength - powmResultLen;
        byte[] result = new byte[modulusLength];

        fixed (byte* ptr = result)
            Gmp.mpz_export((nint)(ptr + offset), out _, 1, 1, 1, nuint.Zero, powmResult);

        return (result, true);
    }

    [Obsolete("This is a previous implementation using BigInteger instead of GMP")]
    public static (byte[], bool) OldRun(byte[] inputData)
    {
        Metrics.ModExpPrecompile++;

        (int baseLength, int expLength, int modulusLength) = GetInputLengths(inputData);

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
    private static UInt256 MultComplexity(in UInt256 baseLength, in UInt256 modulusLength, bool isEip7883Enabled)
    {
        UInt256 maxLength = UInt256.Max(baseLength, modulusLength);
        UInt256.Mod(maxLength, 8, out UInt256 mod8);
        UInt256 words = (maxLength / 8) + (mod8.IsZero ? UInt256.Zero : UInt256.One);

        if (isEip7883Enabled)
        {
            return maxLength > 32 ? 2 * words * words : 16;
        }

        return words * words;
    }

    static readonly UInt256 IterationCountMultiplierEip2565 = 8;

    static readonly UInt256 IterationCountMultiplierEip7883 = 16;

    /// <summary>
    /// def calculate_iteration_count(exponent_length, exponent):
    /// iteration_count = 0
    /// if exponent_length &lt;= 32 and exponent == 0: iteration_count = 0
    /// elif exponent_length &lt;= 32: iteration_count = exponent.bit_length() - 1
    /// elif exponent_length > 32: iteration_count = (8 * (exponent_length - 32)) + ((exponent & (2**256 - 1)).bit_length() - 1)
    /// return max(iteration_count, 1)
    /// </summary>
    /// <param name="exponentLength"></param>
    /// <param name="exponent"></param>
    /// <param name="isEip7883Enabled"></param>
    /// <returns></returns>
    private static UInt256 CalculateIterationCount(UInt256 exponentLength, UInt256 exponent, bool isEip7883Enabled)
    {
        try
        {
            UInt256 iterationCount;
            if (exponentLength <= 32)
            {
                iterationCount = exponent.IsZero ? UInt256.Zero : (UInt256)(exponent.BitLen - 1);
            }
            else
            {
                int bitLength = (exponent & UInt256.MaxValue).BitLen;
                if (bitLength > 0)
                {
                    bitLength--;
                }

                bool overflow = UInt256.MultiplyOverflow(exponentLength - 32,
                    isEip7883Enabled ? IterationCountMultiplierEip7883 : IterationCountMultiplierEip2565,
                    out UInt256 multiplicationResult);
                overflow |= UInt256.AddOverflow(multiplicationResult, (UInt256)bitLength, out iterationCount);
                if (overflow)
                {
                    return UInt256.MaxValue;
                }
            }

            return UInt256.Max(iterationCount, UInt256.One);
        }
        catch (OverflowException)
        {
            return UInt256.MaxValue;
        }
    }
}
