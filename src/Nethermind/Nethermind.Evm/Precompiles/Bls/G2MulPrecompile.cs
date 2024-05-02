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
public class G2MulPrecompile : IPrecompile<G2MulPrecompile>
{
    public static G2MulPrecompile Instance = new G2MulPrecompile();

    private G2MulPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x0f);

    public long BaseGasCost(IReleaseSpec releaseSpec)
    {
        return 45000L;
    }

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        return 0L;
    }

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        const int expectedInputLength = 4 * BlsParams.LenFp + BlsParams.LenFr;
        if (inputData.Length != expectedInputLength)
        {
            return (Array.Empty<byte>(), false);
        }

        // Span<byte> inputDataSpan = stackalloc byte[4 * BlsParams.LenFp + BlsParams.LenFr];
        // inputData.PrepareEthInput(inputDataSpan);

        (byte[], bool) result;

        Span<byte> output = stackalloc byte[4 * BlsParams.LenFp];
        bool success = SubgroupChecks.G2IsInSubGroup(inputData.Span[..(4 * BlsParams.LenFp)])
            && Pairings.BlsG2Mul(inputData.Span, output);
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
