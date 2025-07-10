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
        return RunInternal(inputData);
    }

    private static (byte[], bool) RunInternal(ReadOnlyMemory<byte> inputData)
    {
        Span<byte> inputDataSpan = stackalloc byte[128];

        if (inputData.Length == inputDataSpan.Length)
            inputData.Span.CopyTo(inputDataSpan);
        else
           inputData.PrepareEthInput(inputDataSpan);
        return RunInternal(inputDataSpan);
    }

    private static (byte[], bool) RunInternal(Span<byte> inputDataSpan)
    {
        byte[] output = GC.AllocateUninitializedArray<byte>(64);
        return BN254.Add(inputDataSpan, output) ? (output, true) : IPrecompile.Failure;
    }
}
