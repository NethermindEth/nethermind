// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;

namespace Nethermind.Evm.Precompiles.Snarks;

/// <summary>
/// https://github.com/herumi/mcl/blob/master/api.md
/// </summary>
public class Bn254MulPrecompile : IPrecompile<Bn254MulPrecompile>
{
    public static readonly Bn254MulPrecompile Instance = new();

    public static Address Address { get; } = Address.FromNumber(7);

    public static string Name => "BN256_MUL";

    public long BaseGasCost(IReleaseSpec releaseSpec) => releaseSpec.IsEip1108Enabled ? 6000L : 40000L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    public (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.Bn254MulPrecompile++;
        return inputData.Length == 96 ? RunInternal(inputData.Span) : RunInternal(inputData);
    }

    private static (byte[], bool) RunInternal(ReadOnlyMemory<byte> inputData)
    {
        Span<byte> inputDataSpan = stackalloc byte[96];
        inputData.PrepareEthInput(inputDataSpan);
        return RunInternal(inputDataSpan);
    }

    private static (byte[], bool) RunInternal(ReadOnlySpan<byte> inputDataSpan)
    {
        byte[] output = GC.AllocateUninitializedArray<byte>(64);
        return Pairings.Bn254Mul(inputDataSpan, output) ? (output, true) : IPrecompile.Failure;
    }
}
