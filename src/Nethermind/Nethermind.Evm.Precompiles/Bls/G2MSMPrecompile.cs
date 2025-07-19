// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Collections;
using System.Threading.Tasks;

using G2 = Nethermind.Crypto.Bls.P2;
using System.Runtime.CompilerServices;
using Nethermind.Evm.Precompiles;

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

    public static Address Address { get; } = Address.FromNumber(0xe);

    public static string Name => "BLS12_G2MSM";

    public long BaseGasCost(IReleaseSpec releaseSpec) => 0L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        int k = inputData.Length / ItemSize;
        return 22500L * k * Discount.ForG2(k) / 1000;
    }

    public const int ItemSize = 288;

    [SkipLocalsInit]
    public (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec, bool isCacheable)
    {
        Metrics.BlsG2MSMPrecompile++;

        if (inputData.Length % ItemSize > 0 || inputData.Length == 0)
        {
            return IPrecompile.Failure;
        }

        // use Mul to optimise single point multiplication
        int nItems = inputData.Length / ItemSize;
        return nItems == 1 ? Mul(inputData) : MSM(inputData, nItems);
    }


    private (byte[], bool) Mul(ReadOnlyMemory<byte> inputData)
    {
        G2 x = new(stackalloc long[G2.Sz]);
        if (!x.TryDecodeRaw(inputData[..BlsConst.LenG2].Span) || !(BlsConst.DisableSubgroupChecks || x.InGroup()))
        {
            return IPrecompile.Failure;
        }

        // multiplying by zero gives infinity point
        // any scalar multiplied by infinity point is infinity point
        bool scalarIsZero = !inputData[BlsConst.LenG2..].Span.ContainsAnyExcept((byte)0);
        if (scalarIsZero || x.IsInf())
        {
            return (BlsConst.G2Inf, true);
        }

        Span<byte> scalar = stackalloc byte[32];
        inputData.Span[BlsConst.LenG2..].CopyTo(scalar);
        scalar.Reverse();

        G2 res = x.Mult(scalar);
        return (res.EncodeRaw(), true);
    }

    private (byte[], bool) MSM(ReadOnlyMemory<byte> inputData, int nItems)
    {
        using ArrayPoolList<long> pointBuffer = new(nItems * G2.Sz, nItems * G2.Sz);
        using ArrayPoolList<byte> scalarBuffer = new(nItems * 32, nItems * 32);
        using ArrayPoolList<int> pointDestinations = new(nItems);

        // calculate where in rawPoints buffer decoded points should go
        int npoints = 0;
        for (int i = 0; i < nItems; i++)
        {
            int offset = i * ItemSize;
            ReadOnlySpan<byte> rawPoint = inputData[offset..(offset + BlsConst.LenG2)].Span;

            // exclude infinity points
            int dest = rawPoint.ContainsAnyExcept((byte)0) ? npoints++ : -1;
            pointDestinations.Add(dest);
        }

        // only infinity points so return infinity
        if (npoints == 0)
        {
            return (BlsConst.G2Inf, true);
        }

        bool fail = false;

        // decode points to rawPoints buffer
        // n.b. subgroup checks carried out as part of decoding
#pragma warning disable CS0162 // Unreachable code detected
        if (BlsConst.DisableConcurrency)
        {
            for (int i = 0; i < pointDestinations.Count; i++)
            {
                if (!BlsExtensions.TryDecodeG2ToBuffer(inputData, pointBuffer.AsMemory(), scalarBuffer.AsMemory(), pointDestinations[i], i))
                {
                    fail = true;
                    break;
                }
            }
        }
        else
        {
            Parallel.ForEach(pointDestinations, (dest, state, i) =>
            {
                int index = (int)i;
                if (!BlsExtensions.TryDecodeG2ToBuffer(inputData, pointBuffer.AsMemory(), scalarBuffer.AsMemory(), dest, index))
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

        // compute res = rawPoints_0 * rawScalars_0 + rawPoints_1 * rawScalars_1 + ...
        G2 res = new G2(stackalloc long[G2.Sz]).MultiMult(pointBuffer.AsSpan(), scalarBuffer.AsSpan(), npoints);
        return (res.EncodeRaw(), true);
    }
}
