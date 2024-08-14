// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;

namespace Nethermind.Evm.Precompiles.Snarks;

/// <summary>
/// https://github.com/herumi/mcl/blob/master/api.md
/// </summary>
public class Bn254MulPrecompile : IPrecompile<Bn254MulPrecompile>
{
    public static Bn254MulPrecompile Instance = new Bn254MulPrecompile();

    public static Address Address { get; } = Address.FromNumber(7);

    public long BaseGasCost(IReleaseSpec releaseSpec) => releaseSpec.IsEip1108Enabled ? 6000L : 40000L;

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.Bn254MulPrecompile++;
        Span<byte> inputDataSpan = stackalloc byte[96];
        inputData.PrepareEthInput(inputDataSpan);

        Span<byte> output = stackalloc byte[64];
        return Pairings.Bn254Mul(inputDataSpan, output) ? (output.ToArray(), true) : IPrecompile.Failure;

    }
}
