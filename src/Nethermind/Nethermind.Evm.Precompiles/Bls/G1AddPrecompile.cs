// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using G1 = Nethermind.Crypto.Bls.P1;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class G1AddPrecompile : IPrecompile<G1AddPrecompile>
{
    public static readonly G1AddPrecompile Instance = new();

    private G1AddPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x0b);

    public static string Name => "BLS12_G1ADD";

    public long BaseGasCost(IReleaseSpec releaseSpec) => 375L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    [SkipLocalsInit]
    public (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.BlsG1AddPrecompile++;

        const int expectedInputLength = 2 * BlsConst.LenG1;
        if (inputData.Length != expectedInputLength)
        {
            return IPrecompile.Failure;
        }

        G1 x = new(stackalloc long[G1.Sz]);
        G1 y = new(stackalloc long[G1.Sz]);
        if (!x.TryDecodeRaw(inputData[..BlsConst.LenG1].Span) || !y.TryDecodeRaw(inputData[BlsConst.LenG1..].Span))
        {
            return IPrecompile.Failure;
        }

        // adding to infinity point has no effect
        if (x.IsInf())
        {
            return (inputData[BlsConst.LenG1..].ToArray(), true);
        }

        if (y.IsInf())
        {
            return (inputData[..BlsConst.LenG1].ToArray(), true);
        }

        G1 res = x.Add(y);
        return (res.EncodeRaw(), true);
    }
}
