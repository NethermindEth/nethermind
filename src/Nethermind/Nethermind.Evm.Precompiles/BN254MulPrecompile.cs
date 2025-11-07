// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

/// <see href="https://eips.ethereum.org/EIPS/eip-196" />
public class BN254MulPrecompile : IPrecompile<BN254MulPrecompile>
{
    private const int InputLength = 96;
    private const int OutputLength = 64;

    public static readonly BN254MulPrecompile Instance = new();

    public static Address Address { get; } = Address.FromNumber(7);

    /// <see href="https://eips.ethereum.org/EIPS/eip-7910" />
    public static string Name => "BN254_MUL";

    /// <see href="https://eips.ethereum.org/EIPS/eip-1108" />
    public long BaseGasCost(IReleaseSpec releaseSpec) => releaseSpec.IsEip1108Enabled ? 6_000L : 40_000L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    [SkipLocalsInit]
    public (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.Bn254MulPrecompile++;

        ReadOnlySpan<byte> input = inputData.Span;
        if (InputLength < (uint)input.Length)
        {
            // Input is too long - trim to the expected length.
            input = input[..InputLength];
        }

        byte[] output = new byte[OutputLength];
        bool result = (input.Length == InputLength) ?
            BN254.Mul(output, input) :
            RunPaddedInput(output, input);

        return result ? (output, true) : IPrecompile.Failure;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RunPaddedInput(byte[] output, ReadOnlySpan<byte> input)
    {
        // Input is too short - pad with zeros up to the expected length.
        Span<byte> padded = stackalloc byte[InputLength];
        // Copies input bytes; rest of the span is already zero-initialized.
        input.CopyTo(padded);

        return BN254.Mul(output, padded);
    }
}
