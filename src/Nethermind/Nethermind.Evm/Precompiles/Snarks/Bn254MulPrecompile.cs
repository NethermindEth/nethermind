// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles.Snarks;

public class Bn254MulPrecompile : IPrecompile<Bn254MulPrecompile>
{
    public static readonly Bn254MulPrecompile Instance = new();

    public static Address Address { get; } = Address.FromNumber(7);

    public long BaseGasCost(IReleaseSpec releaseSpec) => releaseSpec.IsEip1108Enabled ? 6000L : 40000L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    public (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.Bn254MulPrecompile++;
        return RunInternal(inputData);
    }

    private static (byte[], bool) RunInternal(ReadOnlyMemory<byte> inputData)
    {
        Span<byte> inputDataSpan = stackalloc byte[96];

        if (inputData.Length == inputDataSpan.Length)
            inputData.Span.CopyTo(inputDataSpan);
        else
            inputData.PrepareEthInput(inputDataSpan);
        return RunInternal(inputDataSpan);
    }

    private static (byte[], bool) RunInternal(Span<byte> inputDataSpan)
    {
        byte[] output = GC.AllocateUninitializedArray<byte>(64);
        return BN254.Mul(inputDataSpan, output) ? (output, true) : IPrecompile.Failure;
    }
}
