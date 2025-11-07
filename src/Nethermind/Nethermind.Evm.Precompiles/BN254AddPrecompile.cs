// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

/// <see href="https://eips.ethereum.org/EIPS/eip-196" />
public class BN254AddPrecompile : IPrecompile<BN254AddPrecompile>
{
    private const int InputLength = 128;
    private const int OutputLength = 64;

    public static readonly BN254AddPrecompile Instance = new();

    public static Address Address { get; } = Address.FromNumber(6);

    /// <see href="https://eips.ethereum.org/EIPS/eip-7910" />
    public static string Name => "BN254_ADD";

    /// <see href="https://eips.ethereum.org/EIPS/eip-1108" />
    public long BaseGasCost(IReleaseSpec releaseSpec) => releaseSpec.IsEip1108Enabled ? 150L : 500L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    [SkipLocalsInit]
    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.Bn254AddPrecompile++;

        ReadOnlySpan<byte> input = inputData.Span;
        if (InputLength < (uint)input.Length)
        {
            // Input is too long - trim to the expected length.
            input = input[..InputLength];
        }

        byte[] output = new byte[OutputLength];
        bool result = (input.Length == InputLength) ?
            BN254.Add(output, input) :
            RunPaddedInput(output, input);

        return result ? output : Errors.Failed;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RunPaddedInput(byte[] output, ReadOnlySpan<byte> input)
    {
        // Input is too short - pad with zeros up to the expected length.
        Span<byte> padded = stackalloc byte[InputLength];
        // Copies input bytes; rest of the span is already zero-initialized.
        input.CopyTo(padded);
        return BN254.Add(output, padded);
    }
}
