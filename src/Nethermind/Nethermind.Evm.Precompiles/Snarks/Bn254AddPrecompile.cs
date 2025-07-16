// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.Precompiles.Snarks;

/// <summary>
/// https://github.com/matter-labs/eip1962/blob/master/eip196_header.h
/// </summary>
public class Bn254AddPrecompile : IPrecompile<Bn254AddPrecompile>
{
    public static readonly Bn254AddPrecompile Instance = new();

    public static Address Address { get; } = Address.FromNumber(6);

    public long BaseGasCost(IReleaseSpec releaseSpec) => releaseSpec.IsEip1108Enabled ? 150L : 500L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    public unsafe (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.Bn254AddPrecompile++;
        return inputData.Length == 128 ? RunInternal(inputData.Span) : RunInternal(inputData);
    }

    private static (byte[], bool) RunInternal(ReadOnlyMemory<byte> inputData)
    {
        Span<byte> inputDataSpan = stackalloc byte[128];
        inputData.PrepareEthInput(inputDataSpan);
        return RunInternal(inputDataSpan);
    }

    private static (byte[], bool) RunInternal(ReadOnlySpan<byte> inputDataSpan)
    {
        byte[] output = GC.AllocateUninitializedArray<byte>(64);
        return Pairings.Bn254Add(inputDataSpan, output) ? (output, true) : IPrecompile.Failure;
    }
}
