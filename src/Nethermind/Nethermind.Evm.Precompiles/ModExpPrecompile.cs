// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.GmpBindings;
using Nethermind.Int256;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// https://github.com/ethereum/EIPs/blob/vbuterin-patch-2/EIPS/bigint_modexp.md
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

    private const int LengthSize = 32;
    private const int StartBaseLength = 0;
    private const int StartExpLength = 32;
    private const int StartModLength = 64;
    private const int LengthsLengths = StartModLength + LengthSize;

    private ModExpPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(5);

    public static string Name => "MODEXP";

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
            ReadOnlySpan<byte> span = inputData.Span;
            return span.Length >= LengthsLengths
                ? DataGasCostInternal(span, releaseSpec)
                : DataGasCostShortInternal(span, releaseSpec);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long DataGasCostShortInternal(ReadOnlySpan<byte> inputData, IReleaseSpec releaseSpec)
    {
        Debug.Assert(inputData.Length < LengthsLengths);

        Span<byte> extendedInput = stackalloc byte[LengthsLengths];
        inputData.CopyTo(extendedInput);

        return DataGasCostInternal(extendedInput, releaseSpec);
    }

    private static long DataGasCostInternal(ReadOnlySpan<byte> inputData, IReleaseSpec releaseSpec)
    {
        (uint baseLength, uint expLength, uint modulusLength) = GetInputLengths(inputData);
        if (ExceedsMaxInputSize(releaseSpec, baseLength, expLength, modulusLength))
        {
            return long.MaxValue;
        }

        ulong complexity = MultComplexity(baseLength, modulusLength, releaseSpec.IsEip7883Enabled);

        uint expLengthUpTo32 = Math.Min(LengthSize, expLength);
        uint startIndex = LengthsLengths + baseLength; //+ expLength - expLengthUpTo32; // Geth takes head here, why?
        UInt256 exp = new(inputData.SliceWithZeroPaddingEmptyOnError((int)startIndex, (int)expLengthUpTo32), isBigEndian: true);
        UInt256 iterationCount = CalculateIterationCount(expLength, exp, releaseSpec.IsEip7883Enabled);

        bool overflow = UInt256.MultiplyOverflow(complexity, iterationCount, out UInt256 result);
        result /= 3;
        return result > long.MaxValue || overflow
            ? long.MaxValue
            : Math.Max(releaseSpec.IsEip7883Enabled ? GasCostOf.MinModExpEip7883 : GasCostOf.MinModExpEip2565, (long)result);
    }

    private static bool ExceedsMaxInputSize(IReleaseSpec releaseSpec, uint baseLength, uint expLength, uint modulusLength)
    {
        return releaseSpec.IsEip7823Enabled
            ? baseLength > ModExpMaxInputSizeEip7823 || expLength > ModExpMaxInputSizeEip7823 || modulusLength > ModExpMaxInputSizeEip7823
            : baseLength == int.MaxValue || modulusLength == int.MaxValue;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private (uint baseLength, uint expLength, uint modulusLength) GetInputLengthsShort(ReadOnlySpan<byte> inputData)
    {
        Debug.Assert(inputData.Length < LengthsLengths);

        Span<byte> extendedInput = stackalloc byte[LengthsLengths];
        inputData.CopyTo(extendedInput);

        return GetInputLengths(extendedInput);
    }

    private static (uint baseLength, uint expLength, uint modulusLength) GetInputLengths(ReadOnlySpan<byte> inputData)
    {
        // Test if too high
        if (Vector256<byte>.IsSupported)
        {
            ref var firstByte = ref MemoryMarshal.GetReference(inputData);
            Vector256<byte> mask = ~Vector256.Create(0, 0, 0, 0, 0, 0, 0, uint.MaxValue).AsByte();
            if (Vector256.BitwiseAnd(
                    Vector256.BitwiseOr(
                        Vector256.BitwiseOr(
                            Unsafe.ReadUnaligned<Vector256<byte>>(ref firstByte),
                            Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref firstByte, StartExpLength))),
                        Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref firstByte, 64))),
                mask) != Vector256<byte>.Zero)
            {
                return GetInputLengthsMayOverflow(inputData);
            }
        }
        else if (Vector128<byte>.IsSupported)
        {
            ref var firstByte = ref MemoryMarshal.GetReference(inputData);
            Vector128<byte> mask = ~Vector128.Create(0, 0, 0, uint.MaxValue).AsByte();
            if (Vector128.BitwiseOr(
                    Vector128.BitwiseOr(
                        Vector128.BitwiseOr(
                            Unsafe.ReadUnaligned<Vector128<byte>>(ref firstByte),
                            Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref firstByte, StartExpLength))),
                        Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref firstByte, 64))),
                    Vector128.BitwiseAnd(
                        Vector128.BitwiseOr(
                            Vector128.BitwiseOr(
                                Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref firstByte, 16)),
                                Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref firstByte, StartExpLength + 16))),
                            Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref firstByte, StartModLength + 16))),
                        mask)
                ) != Vector128<byte>.Zero)
            {
                return GetInputLengthsMayOverflow(inputData);
            }
        }
        else if (inputData.Slice(StartBaseLength, LengthSize - sizeof(uint)).IndexOfAnyExcept((byte)0) >= 0 ||
                inputData.Slice(StartExpLength, LengthSize - sizeof(uint)).IndexOfAnyExcept((byte)0) >= 0 ||
                inputData.Slice(StartModLength, LengthSize - sizeof(uint)).IndexOfAnyExcept((byte)0) >= 0)
        {
            return GetInputLengthsMayOverflow(inputData);
        }

        uint baseLength = BinaryPrimitives.ReadUInt32BigEndian(inputData.Slice(32 - sizeof(uint), sizeof(uint)));
        uint expLength = BinaryPrimitives.ReadUInt32BigEndian(inputData.Slice(64 - sizeof(uint), sizeof(uint)));
        uint modulusLength = BinaryPrimitives.ReadUInt32BigEndian(inputData.Slice(96 - sizeof(uint), sizeof(uint)));
        return (baseLength, expLength, modulusLength);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (uint baseLength, uint expLength, uint modulusLength) GetInputLengthsMayOverflow(ReadOnlySpan<byte> inputData)
    {
        // Only valid if baseLength and modulusLength are zero; when expLength doesn't matter
        if (Vector256<byte>.IsSupported)
        {
            ref var firstByte = ref MemoryMarshal.GetReference(inputData);
            if (Vector256.BitwiseOr(
                    Unsafe.ReadUnaligned<Vector256<byte>>(ref firstByte),
                    Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref firstByte, StartModLength)))
                != Vector256<byte>.Zero)
            {
                // Overflow
                return (uint.MaxValue, uint.MaxValue, uint.MaxValue);
            }
        }
        else if (Vector128<byte>.IsSupported)
        {
            ref var firstByte = ref MemoryMarshal.GetReference(inputData);
            if (Vector128.BitwiseOr(
                    Vector128.BitwiseOr(
                        Unsafe.ReadUnaligned<Vector128<byte>>(ref firstByte),
                        Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref firstByte, 16))),
                    Vector128.BitwiseOr(
                        Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref firstByte, StartModLength)),
                        Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref firstByte, StartModLength + 16)))
                ) != Vector128<byte>.Zero)
            {
                // Overflow
                return (uint.MaxValue, uint.MaxValue, uint.MaxValue);
            }
        }
        else if (inputData.Slice(StartBaseLength, LengthSize).IndexOfAnyExcept((byte)0) >= 0 ||
                inputData.Slice(StartModLength, LengthSize).IndexOfAnyExcept((byte)0) >= 0)
        {
            // Overflow
            return (uint.MaxValue, uint.MaxValue, uint.MaxValue);
        }

        return (0, uint.MaxValue, 0);
    }

    public unsafe (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.ModExpPrecompile++;

        ReadOnlySpan<byte> inputSpan = inputData.Span;
        (uint baseLength, uint expLength, uint modulusLength) = inputSpan.Length >= LengthsLengths
                ? GetInputLengths(inputSpan)
                : GetInputLengthsShort(inputSpan);

        if (ExceedsMaxInputSize(releaseSpec, baseLength, expLength, modulusLength))
            return IPrecompile.Failure;

        // if both are 0, then expLength can be huge, which leads to a potential buffer too big exception
        if (baseLength == 0 && modulusLength == 0)
            return (Bytes.Empty, true);

        using var modulusInt = mpz_t.Create();

        ReadOnlySpan<byte> modulusDataSpan = inputSpan.SliceWithZeroPaddingEmptyOnError(96 + (int)baseLength + (int)expLength, (int)modulusLength);
        if (modulusDataSpan.Length > 0)
        {
            fixed (byte* modulusData = &MemoryMarshal.GetReference(modulusDataSpan))
                Gmp.mpz_import(modulusInt, modulusLength, 1, 1, 1, nuint.Zero, (nint)modulusData);
        }

        if (Gmp.mpz_sgn(modulusInt) == 0)
            return (new byte[modulusLength], true);

        using var baseInt = mpz_t.Create();
        using var expInt = mpz_t.Create();
        using var powmResult = mpz_t.Create();

        ReadOnlySpan<byte> baseDataSpan = inputSpan.SliceWithZeroPaddingEmptyOnError(96, (int)baseLength);
        if (baseDataSpan.Length > 0)
        {
            fixed (byte* baseData = &MemoryMarshal.GetReference(baseDataSpan))
                Gmp.mpz_import(baseInt, baseLength, 1, 1, 1, nuint.Zero, (nint)baseData);
        }

        ReadOnlySpan<byte> expDataSpan = inputSpan.SliceWithZeroPaddingEmptyOnError(96 + (int)baseLength, (int)expLength);
        if (expDataSpan.Length > 0)
        {
            fixed (byte* expData = &MemoryMarshal.GetReference(expDataSpan))
                Gmp.mpz_import(expInt, expLength, 1, 1, 1, nuint.Zero, (nint)expData);
        }

        Gmp.mpz_powm(powmResult, baseInt, expInt, modulusInt);

        nint powmResultLen = (nint)(Gmp.mpz_sizeinbase(powmResult, 2) + 7) / 8;
        nint offset = (int)modulusLength - powmResultLen;

        byte[] result = new byte[modulusLength];
        fixed (byte* ptr = &MemoryMarshal.GetArrayDataReference(result))
            Gmp.mpz_export((nint)(ptr + offset), out _, 1, 1, 1, nuint.Zero, powmResult);

        return (result, true);
    }

    /// <summary>
    /// def calculate_multiplication_complexity(base_length, modulus_length):
    /// max_length = max(base_length, modulus_length)
    /// words = math.ceil(max_length / 8)
    /// return words**2
    /// </summary>
    /// <returns></returns>
    private static ulong MultComplexity(uint baseLength, uint modulusLength, bool isEip7883Enabled)
    {
        // Pick the larger of the two  
        uint max = baseLength > modulusLength ? baseLength : modulusLength;

        // Compute ceil(max/8) via a single add + shift
        // (max + 7) >> 3  ==  (max + 7) / 8, rounding up
        ulong words = ((ulong)max + 7u) >> 3;

        // Square it once
        ulong sq = words * words;

        // If EIP-7883 => small-case = 16, else 2*sq when max>32
        if (isEip7883Enabled)
        {
            return max > LengthSize
                ? (sq << 1)    // 2 * words * words
                : 16UL;        // constant floor
        }

        // Otherwise plain square
        return sq;
    }

    static readonly ulong IterationCountMultiplierEip2565 = 8;

    static readonly ulong IterationCountMultiplierEip7883 = 16;

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
    private static UInt256 CalculateIterationCount(uint exponentLength, UInt256 exponent, bool isEip7883Enabled)
    {
        ulong iterationCount;
        uint overflow = 0;
        if (exponentLength <= LengthSize)
        {
            iterationCount = (uint)Math.Max(1, exponent.BitLen - 1);
        }
        else
        {
            uint bitLength = (uint)exponent.BitLen;
            if (bitLength > 0)
            {
                bitLength--;
            }

            ulong multiplicationResult = (exponentLength - LengthSize) * (isEip7883Enabled ? IterationCountMultiplierEip7883 : IterationCountMultiplierEip2565);
            iterationCount = multiplicationResult + bitLength;
            if (iterationCount < multiplicationResult)
            {
                // Overflowed
                overflow = 1;
            }
            else if (iterationCount < 1)
            {
                // Min 1 iteration
                iterationCount = 1;
            }
        }

        return new UInt256(iterationCount, overflow);
    }
}
