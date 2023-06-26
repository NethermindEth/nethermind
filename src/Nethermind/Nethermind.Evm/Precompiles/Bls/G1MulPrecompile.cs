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
public class G1MulPrecompile : IPrecompile
{
    public static IPrecompile Instance = new G1MulPrecompile();

    private G1MulPrecompile()
    {
    }

    public Address Address { get; } = Address.FromNumber(0x0d);

    public long BaseGasCost(IReleaseSpec releaseSpec)
    {
        return 12000L;
    }

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        return 0L;
    }

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        const int expectedInputLength = 2 * BlsParams.LenFp + BlsParams.LenFr;
        if (inputData.Length != expectedInputLength)
        {
            return (Array.Empty<byte>(), false);
        }

        // Span<byte> inputDataSpan = stackalloc byte[expectedInputLength];
        // inputData.PrepareEthInput(inputDataSpan);

        (byte[], bool) result;

        Span<byte> output = stackalloc byte[2 * BlsParams.LenFp];
        bool success = Pairings.BlsG1Mul(inputData.Span, output);
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
