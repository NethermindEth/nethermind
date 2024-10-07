// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using System.Threading.Tasks;
using Nethermind.Core.Collections;

using G1 = Nethermind.Crypto.Bls.P1;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class G1MSMPrecompile : IPrecompile<G1MSMPrecompile>
{
    public static readonly G1MSMPrecompile Instance = new();

    private G1MSMPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x0d);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 0L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        int k = inputData.Length / ItemSize;
        return 12000L * k * Discount.For(k) / 1000;
    }

    private const int ItemSize = 160;

    public (ReadOnlyMemory<byte>, bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.BlsG1MSMPrecompile++;

        if (inputData.Length % ItemSize > 0 || inputData.Length == 0)
        {
            return IPrecompile.Failure;
        }

        int nItems = inputData.Length / ItemSize;

        using ArrayPoolList<long> rawPoints = new(nItems * 18, nItems * 18);
        using ArrayPoolList<byte> rawScalars = new(nItems * 32, nItems * 32);
        using ArrayPoolList<int> pointDestinations = new(nItems);

        int npoints = 0;
        for (int i = 0; i < nItems; i++)
        {
            int offset = i * ItemSize;
            ReadOnlySpan<byte> rawPoint = inputData[offset..(offset + BlsConst.LenG1)].Span;

            // exclude infinity points
            int dest = rawPoint.ContainsAnyExcept((byte)0) ? npoints++ : -1;
            pointDestinations.Add(dest);
        }

        if (npoints == 0)
        {
            return (BlsConst.G1Inf, true);
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

        G1 res = new G1().MultiMult(rawPoints.AsSpan(), rawScalars.AsSpan(), npoints);
        return (res.EncodeRaw(), true);
    }

    private static bool TryDecodePoint(ReadOnlyMemory<byte> inputData, Span<long> rawPoints, Span<byte> rawScalars, int dest, int i)
    {
        if (dest == -1)
        {
            return true;
        }

        int offset = i * ItemSize;
        ReadOnlySpan<byte> rawPoint = inputData[offset..(offset + BlsConst.LenG1)].Span;
        ReadOnlySpan<byte> rawScalar = inputData[(offset + BlsConst.LenG1)..(offset + ItemSize)].Span;

        G1 p = new(rawPoints[(dest * 18)..]);

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
