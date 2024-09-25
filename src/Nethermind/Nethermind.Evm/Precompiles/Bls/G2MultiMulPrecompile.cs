// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Collections;

using G2 = Nethermind.Crypto.Bls.P2;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class G2MultiMulPrecompile : IPrecompile<G2MultiMulPrecompile>
{
    public static readonly G2MultiMulPrecompile Instance = new();

    private G2MultiMulPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x10);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 0L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        int k = inputData.Length / ItemSize;
        return 45000L * k * Discount.For(k) / 1000;
    }

    private const int ItemSize = 288;

    public (ReadOnlyMemory<byte>, bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (inputData.Length % ItemSize > 0 || inputData.Length == 0)
        {
            return IPrecompile.Failure;
        }

        int nItems = inputData.Length / ItemSize;

        using ArrayPoolList<long> rawPoints = new(nItems * 36, nItems * 36);
        using ArrayPoolList<byte> rawScalars = new(nItems * 32, nItems * 32);
        using ArrayPoolList<int> pointDestinations = new(nItems);

        int npoints = 0;
        for (int i = 0; i < nItems; i++)
        {
            int offset = i * ItemSize;
            ReadOnlySpan<byte> rawPoint = inputData[offset..(offset + BlsConst.LenG2)].Span;
            ReadOnlySpan<byte> rawScalar = inputData[(offset + BlsConst.LenG2)..(offset + ItemSize)].Span;

            G2 p = new(rawPoints.AsSpan()[(npoints * 36)..]);

            if (!p.TryDecodeRaw(rawPoint) || !p.InGroup())
            {
                return IPrecompile.Failure;
            }

            if (p.IsInf())
            {
                continue;
            }

            int destOffset = npoints * 32;
            rawScalar.CopyTo(rawScalars.AsSpan()[destOffset..]);
            rawScalars.AsSpan()[destOffset..(destOffset + 32)].Reverse();

            npoints++;
        }

        if (npoints == 0)
        {
            return (BlsConst.G2Inf, true);
        }

        G2 res = new G2().MultiMult(rawPoints.AsSpan(), rawScalars.AsSpan(), npoints);
        return (res.EncodeRaw(), true);
    }
}
