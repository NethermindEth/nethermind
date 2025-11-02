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

    public (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.Bn254AddPrecompile++;

        ReadOnlySpan<byte> input = inputData.Span;
        if (input.Length > InputLength)
        {
            // Input is too long — trim to the expected length.
            input = input[..InputLength];
        }
        else if (input.Length != InputLength)
        {
            return PadInput(input);
        }

        byte[] output = new byte[OutputLength];
        return BN254.Add(input, output) ? (output, true) : IPrecompile.Failure;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (byte[], bool) PadInput(ReadOnlySpan<byte> src)
    {
        // Input is too short — pad with zeros up to the expected length.
        Span<byte> padded = stackalloc byte[InputLength];
        // Copies input bytes; rest of the span is already zero-initialized.
        src.CopyTo(padded);

        byte[] output = new byte[OutputLength];
        return BN254.Add(padded, output) ? (output, true) : IPrecompile.Failure;
    }
}
