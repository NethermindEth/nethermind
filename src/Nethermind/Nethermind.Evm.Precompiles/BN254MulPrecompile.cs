// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

/// <see href="https://eips.ethereum.org/EIPS/eip-196" />
public class BN254MulPrecompile : IPrecompile<BN254MulPrecompile>
{
    public static readonly BN254MulPrecompile Instance = new();

    public static Address Address { get; } = Address.FromNumber(7);

    /// <see href="https://eips.ethereum.org/EIPS/eip-7910" />
    public static string Name => "BN256_MUL";

    /// <see href="https://eips.ethereum.org/EIPS/eip-1108" />
    public long BaseGasCost(IReleaseSpec releaseSpec) => releaseSpec.IsEip1108Enabled ? 6_000L : 40_000L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    public (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.Bn254MulPrecompile++;

        Span<byte> input = stackalloc byte[96];
        Span<byte> output = stackalloc byte[64];

        inputData.Span[0..Math.Min(inputData.Length, input.Length)].CopyTo(input);

        return BN254.Mul(input, output) ? (output.ToArray(), true) : IPrecompile.Failure;
    }
}
