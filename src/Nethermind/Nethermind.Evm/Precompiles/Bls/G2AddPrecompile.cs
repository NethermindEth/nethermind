// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.State;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class G2AddPrecompile : IPrecompile<G2AddPrecompile>
{
    public static G2AddPrecompile Instance = new G2AddPrecompile();

    private G2AddPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x0f);

    public long BaseGasCost(IReleaseSpec releaseSpec)
    {
        return 4500L;
    }

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        return 0L;
    }

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec, IWorldState _)
    {
        const int expectedInputLength = 8 * BlsParams.LenFp;
        if (inputData.Length != expectedInputLength)
        {
            return (Array.Empty<byte>(), false);
        }

        // Span<byte> inputDataSpan = stackalloc byte[expectedInputLength];
        // inputData.PrepareEthInput(inputDataSpan);

        (byte[], bool) result;

        Span<byte> output = stackalloc byte[4 * BlsParams.LenFp];
        bool success = Pairings.BlsG2Add(inputData.Span, output);
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
