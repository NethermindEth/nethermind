// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class G1MultiExpPrecompile : IPrecompile<G1MultiExpPrecompile>
{
    public static G1MultiExpPrecompile Instance = new G1MultiExpPrecompile();

    private G1MultiExpPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x0d);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 0L;

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        int k = inputData.Length / ItemSize;
        return 12000L * k * Discount.For(k) / 1000;
    }

    private const int ItemSize = 160;

    [SkipLocalsInit]
    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (inputData.Length % ItemSize > 0 || inputData.Length == 0)
        {
            return IPrecompile.Failure;
        }

        for (int i = 0; i < (inputData.Length / ItemSize); i++)
        {
            int offset = i * ItemSize;
            if (!SubgroupChecks.G1IsInSubGroup(inputData.Span[offset..(offset + (2 * BlsParams.LenFp))]))
            {
                return IPrecompile.Failure;
            }
        }

        Span<byte> output = stackalloc byte[2 * BlsParams.LenFp];
        bool success = Pairings.BlsG1MultiExp(inputData.Span, output);
        return success ? (output.ToArray(), true) : IPrecompile.Failure;
    }
}
