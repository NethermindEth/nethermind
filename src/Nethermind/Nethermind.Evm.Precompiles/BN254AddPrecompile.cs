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
    public static readonly BN254AddPrecompile Instance = new();

    public static Address Address { get; } = Address.FromNumber(6);

    /// <see href="https://eips.ethereum.org/EIPS/eip-7910" />
    public static string Name => "BN254_ADD";

    /// <see href="https://eips.ethereum.org/EIPS/eip-1108" />
    public long BaseGasCost(IReleaseSpec releaseSpec) => releaseSpec.IsEip1108Enabled ? 150L : 500L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    [SkipLocalsInit]
    public (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.Bn254AddPrecompile++;

        Span<byte> inputWritable = stackalloc byte[128];
        ReadOnlySpan<byte> input = inputWritable;

        ReadOnlySpan<byte> orignalInput = inputData.Span;
        if (orignalInput.Length == 128)
        {
            input = orignalInput;
        }
        else if (orignalInput.Length < 128)
        {
            orignalInput.CopyTo(inputWritable);
            inputWritable[orignalInput.Length..].Clear();
        }
        else
        {
            orignalInput[0..inputWritable.Length].CopyTo(inputWritable);
        }

        byte[] output = new byte[64];
        return BN254.Add(input, output) ? (output, true) : IPrecompile.Failure;
    }
}
