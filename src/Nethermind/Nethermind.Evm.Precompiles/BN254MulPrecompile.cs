// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// <see href="https://eips.ethereum.org/EIPS/eip-196" />
/// </summary>
public partial class BN254MulPrecompile : IPrecompile<BN254MulPrecompile>
{
    private const int InputLength = 96;
    private const int OutputLength = 64;

    public static BN254MulPrecompile Instance { get; } = new();

    public static Address Address { get; } = Address.FromNumber(7);

    /// <see href="https://eips.ethereum.org/EIPS/eip-7910" />
    public static string Name => "BN254_MUL";

    /// <see href="https://eips.ethereum.org/EIPS/eip-1108" />
    public long BaseGasCost(IReleaseSpec releaseSpec) => releaseSpec.IsEip1108Enabled ? 6_000L : 40_000L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec _) => 0L;

    public ReadOnlyMemory<byte> NormalizeInput(ReadOnlyMemory<byte> inputData)
    {
        ReadOnlyMemory<byte> clamped = inputData.Length > InputLength ? inputData[..InputLength] : inputData;
        int end = clamped.Span.LastIndexOfAnyExcept((byte)0);
        return end < 0 ? ReadOnlyMemory<byte>.Empty : clamped[..(end + 1)];
    }

    [SkipLocalsInit]
    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
#if !ZK_EVM
        Metrics.Bn254MulPrecompile++;
#endif
        ReadOnlySpan<byte> input = inputData.Span;
        if (InputLength < input.Length)
        {
            // Input is too long - trim to the expected length.
            input = input[..InputLength];
        }

        byte[] output = new byte[OutputLength];
        bool result = input.Length == InputLength
            ? Mul(input, output)
            : RunPaddedInput(input, output);

        return result ? output : Errors.Failed;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RunPaddedInput(ReadOnlySpan<byte> input, byte[] output)
    {
        // Input is too short - pad with zeros up to the expected length.
        Span<byte> padded = stackalloc byte[InputLength];
        // Copies input bytes; rest of the span is already zero-initialized.
        input.CopyTo(padded);

        return Mul(padded, output);
    }
}
