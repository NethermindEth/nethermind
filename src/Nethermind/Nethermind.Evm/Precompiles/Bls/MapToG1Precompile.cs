// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class MapToG1Precompile : IPrecompile<MapToG1Precompile>
{
    public static MapToG1Precompile Instance = new MapToG1Precompile();

    private MapToG1Precompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x12);

    public long BaseGasCost(IReleaseSpec releaseSpec)
    {
        return 5500L;
    }

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        return 0L;
    }

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        const int expectedInputLength = 64;
        if (inputData.Length != expectedInputLength)
        {
            return (Array.Empty<byte>(), false);
        }

        (byte[], bool) result;

        Span<byte> output = stackalloc byte[128];
        bool success = Pairings.BlsMapToG1(inputData.Span, output);
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
