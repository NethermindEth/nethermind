// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class MapToG2Precompile : IPrecompile
{
    public static IPrecompile Instance = new MapToG2Precompile();

    private MapToG2Precompile()
    {
    }

    public Address Address { get; } = Address.FromNumber(0x14);

    public long BaseGasCost(IReleaseSpec releaseSpec)
    {
        return 110000;
    }

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        return 0L;
    }

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        const int expectedInputLength = 2 * BlsParams.LenFp;
        if (inputData.Length != expectedInputLength)
        {
            return (Array.Empty<byte>(), false);
        }

        // Span<byte> inputDataSpan = stackalloc byte[2 * BlsParams.LenFp];
        // inputData.PrepareEthInput(inputDataSpan);

        (byte[], bool) result;

        Span<byte> output = stackalloc byte[4 * BlsParams.LenFp];
        bool success = Pairings.BlsMapToG2(inputData.Span, output);
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
