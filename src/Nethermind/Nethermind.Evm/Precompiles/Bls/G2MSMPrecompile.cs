// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Collections;
using System.Threading.Tasks;

using G2 = Nethermind.Crypto.Bls.P2;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class G2MSMPrecompile : IPrecompile<G2MSMPrecompile>
{
    public static readonly G2MSMPrecompile Instance = new();

    private G2MSMPrecompile()
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
        Metrics.BlsG2MSMPrecompile++;

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

            // exclude infinity points
            int dest = rawPoint.ContainsAnyExcept((byte)0) ? npoints++ : -1;
            pointDestinations.Add(dest);
        }

        if (npoints == 0)
        {
            return (BlsConst.G2Inf, true);
        }

        bool fail = false;

#pragma warning disable CS0162 // Unreachable code detected
        if (BlsConst.DisableConcurrency)
        {
            for (int i = 0; i < pointDestinations.Count; i++)
            {
                if (!TryDecodePoint(inputData, rawPoints.AsSpan(), rawScalars.AsSpan(), pointDestinations[i], i))
                {
                    fail = true;
                    break;
                }
            }
        }
        else
        {
            Parallel.ForEach(pointDestinations, (dest, state, i) => {
                if (!TryDecodePoint(inputData, rawPoints.AsSpan(), rawScalars.AsSpan(), dest, (int)i))
                {
                    fail = true;
                    state.Break();
                }
            });
        }
#pragma warning restore CS0162 // Unreachable code detected

        if (fail)
        {
            return IPrecompile.Failure;
        }

        G2 res = new G2().MultiMult(rawPoints.AsSpan(), rawScalars.AsSpan(), npoints);

        return (res.EncodeRaw(), true);
    }

    private static bool TryDecodePoint(ReadOnlyMemory<byte> inputData, Span<long> rawPoints, Span<byte> rawScalars, int dest, int i)
    {
        if (dest == -1)
        {
            return true;
        }

        int offset = i * ItemSize;
        ReadOnlySpan<byte> rawPoint = inputData[offset..(offset + BlsConst.LenG2)].Span;
        ReadOnlySpan<byte> rawScalar = inputData[(offset + BlsConst.LenG2)..(offset + ItemSize)].Span;

        G2 p = new(rawPoints[(dest * 36)..]);

        if (!p.TryDecodeRaw(rawPoint) || !(BlsConst.DisableSubgroupChecks || p.InGroup()))
        {
            return false;
        }

        int destOffset = dest * 32;
        rawScalar.CopyTo(rawScalars[destOffset..]);
        rawScalars[destOffset..(destOffset + 32)].Reverse();
        return true;
    }
}
