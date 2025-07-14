// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using System.Threading.Tasks;
using Nethermind.Core.Collections;

using G1 = Nethermind.Crypto.Bls.P1;
using System.Runtime.CompilerServices;

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

    public static Address Address { get; } = Address.FromNumber(0x0c);

    public static string Name => "BLS12_G1MSM";
    
    public long BaseGasCost(IReleaseSpec releaseSpec) => 0L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        int k = inputData.Length / ItemSize;
        return 12000L * k * Discount.ForG1(k) / 1000;
    }

    public const int ItemSize = 160;

    [SkipLocalsInit]
    public (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.BlsG1MSMPrecompile++;

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
        G1 x = new(stackalloc long[G1.Sz]);
        if (!x.TryDecodeRaw(inputData[..BlsConst.LenG1].Span) || !(BlsConst.DisableSubgroupChecks || x.InGroup()))
        {
            return IPrecompile.Failure;
        }

        // multiplying by zero gives infinity point
        // any scalar multiplied by infinity point is infinity point
        bool scalarIsZero = !inputData.Span[BlsConst.LenG1..].ContainsAnyExcept((byte)0);
        if (scalarIsZero || x.IsInf())
        {
            return (BlsConst.G1Inf, true);
        }

        Span<byte> scalar = stackalloc byte[32];
        inputData.Span[BlsConst.LenG1..].CopyTo(scalar);
        scalar.Reverse();

        G1 res = x.Mult(scalar);
        return (res.EncodeRaw(), true);
    }

    private (byte[], bool) MSM(ReadOnlyMemory<byte> inputData, int nItems)
    {
        using ArrayPoolList<long> rawPoints = new(nItems * G1.Sz, nItems * G1.Sz);
        using ArrayPoolList<byte> rawScalars = new(nItems * 32, nItems * 32);
        using ArrayPoolList<int> pointDestinations = new(nItems);

        // calculate where in rawPoints buffer decoded points should go
        int npoints = 0;
        for (int i = 0; i < nItems; i++)
        {
            int offset = i * ItemSize;
            ReadOnlySpan<byte> rawPoint = inputData[offset..(offset + BlsConst.LenG1)].Span;

            // exclude infinity points
            int dest = rawPoint.ContainsAnyExcept((byte)0) ? npoints++ : -1;
            pointDestinations.Add(dest);
        }

        // only infinity points so return infinity
        if (npoints == 0)
        {
            return (BlsConst.G1Inf, true);
        }

        bool fail = false;

        // decode points to rawPoints buffer
        // n.b. subgroup checks carried out as part of decoding
#pragma warning disable CS0162 // Unreachable code detected
        if (BlsConst.DisableConcurrency)
        {
            for (int i = 0; i < pointDestinations.Count; i++)
            {
                if (!BlsExtensions.TryDecodeG1ToBuffer(inputData, rawPoints.AsMemory(), rawScalars.AsMemory(), pointDestinations[i], i))
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
                if (!BlsExtensions.TryDecodeG1ToBuffer(inputData, rawPoints.AsMemory(), rawScalars.AsMemory(), dest, index))
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
        G1 res = new G1(stackalloc long[G1.Sz]).MultiMult(rawPoints.AsSpan(), rawScalars.AsSpan(), npoints);
        return (res.EncodeRaw(), true);
    }
}
