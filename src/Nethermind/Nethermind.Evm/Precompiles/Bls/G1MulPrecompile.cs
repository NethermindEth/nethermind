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
public class G1MulPrecompile : IPrecompile<G1MulPrecompile>
{
    public static G1MulPrecompile Instance = new G1MulPrecompile();

    private G1MulPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x0c);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 12000L;

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    [SkipLocalsInit]
    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        const int expectedInputLength = 2 * BlsParams.LenFp + BlsParams.LenFr;
        if (inputData.Length != expectedInputLength)
        {
            return IPrecompile.Failure;
        }

        Span<byte> output = stackalloc byte[2 * BlsParams.LenFp];
        bool success = SubgroupChecks.G1IsInSubGroup(inputData.Span[..(2 * BlsParams.LenFp)])
            && Pairings.BlsG1Mul(inputData.Span, output);

        return success ? (output.ToArray(), true) : IPrecompile.Failure;
    }
}
