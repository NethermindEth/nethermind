// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;

using G2 = Nethermind.Crypto.Bls.P2;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class G2MulPrecompile : IPrecompile<G2MulPrecompile>
{
    public static readonly G2MulPrecompile Instance = new();

    private G2MulPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x0f);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 45000L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    [SkipLocalsInit]
    public (ReadOnlyMemory<byte>, bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.BlsG2MulPrecompile++;

        const int expectedInputLength = BlsConst.LenG2 + BlsConst.LenFr;

        if (inputData.Length != expectedInputLength)
        {
            return IPrecompile.Failure;
        }

        G2 x = new(stackalloc long[G2.Sz]);
        if (!x.TryDecodeRaw(inputData[..BlsConst.LenG2].Span) || !(BlsConst.DisableSubgroupChecks || x.InGroup()))
        {
            return IPrecompile.Failure;
        }

        bool scalarIsInfinity = !inputData[BlsConst.LenG2..].Span.ContainsAnyExcept((byte)0);
        if (scalarIsInfinity || x.IsInf())
        {
            return (BlsConst.G2Inf, true);
        }

        Span<byte> scalar = stackalloc byte[32];
        inputData.Span[BlsConst.LenG2..].CopyTo(scalar);
        scalar.Reverse();

        G2 res = x.Mult(scalar);
        return (res.EncodeRaw(), true);
    }
}
