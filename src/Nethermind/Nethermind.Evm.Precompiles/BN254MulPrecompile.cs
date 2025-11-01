// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

    public (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.Bn254MulPrecompile++;

        Span<byte> input = stackalloc byte[InputLength];

        ReadOnlySpan<byte> inputSpan = inputData.Span;
        inputSpan[0..Math.Min(inputSpan.Length, input.Length)].CopyTo(input);

        byte[] output = new byte[OutputLength];
        return BN254.Mul(input, output) ? (output, true) : IPrecompile.Failure;
    }
}
