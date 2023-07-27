// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.State;

namespace Nethermind.Evm.Precompiles.Snarks;

/// <summary>
/// https://github.com/matter-labs/eip1962/blob/master/eip196_header.h
/// </summary>
public class Bn254AddPrecompile : IPrecompile<Bn254AddPrecompile>
{
    public static Bn254AddPrecompile Instance = new Bn254AddPrecompile();

    public static Address Address { get; } = Address.FromNumber(6);

    public long BaseGasCost(IReleaseSpec releaseSpec)
    {
        return releaseSpec.IsEip1108Enabled ? 150L : 500L;
    }

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        return 0L;
    }

    public unsafe (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec, IWorldState? _ = null)
    {
        Metrics.Bn254AddPrecompile++;
        Span<byte> inputDataSpan = stackalloc byte[128];
        inputData.PrepareEthInput(inputDataSpan);

        Span<byte> output = stackalloc byte[64];
        bool success = Pairings.Bn254Add(inputDataSpan, output);

        (byte[], bool) result;
        if (success)
        {
            result = (output.ToArray(), true);
        }
        else
        {
            result = (Array.Empty<byte>(), false);
        }

        return result;
    }
}
