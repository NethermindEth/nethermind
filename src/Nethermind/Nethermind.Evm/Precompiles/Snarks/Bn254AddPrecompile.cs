// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles.Snarks;

public class Bn254AddPrecompile : IPrecompile<Bn254AddPrecompile>
{
    public static readonly Bn254AddPrecompile Instance = new();

    public static Address Address { get; } = Address.FromNumber(6);

    public long BaseGasCost(IReleaseSpec releaseSpec) => releaseSpec.IsEip1108Enabled ? 150L : 500L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    public unsafe (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.Bn254AddPrecompile++;

        Span<byte> input = stackalloc byte[128];
        Span<byte> output = stackalloc byte[64];

        inputData.Span[0..Math.Min(inputData.Length, input.Length)].CopyTo(input);

        return BN254.Add(input, output) ? (output.ToArray(), true) : IPrecompile.Failure;
    }
}
