// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using G1 = Nethermind.Crypto.Bls.P1;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;

namespace Nethermind.Evm.Precompiles;

public partial class Bls12381G1MsmPrecompile
{
    [SkipLocalsInit]
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        Metrics.Bls12381G1MsmPrecompile++;

        if (!ValidateInputLength(inputData))
            return Errors.InvalidInputLength;

        // use Mul to optimize single point multiplication
        int nItems = inputData.Length / ItemSize;
        return nItems == 1 ? Mul(inputData) : Msm(inputData, nItems);
    }

    private Result<byte[]> Mul(ReadOnlyMemory<byte> inputData)
    {
        G1 x = new(stackalloc long[G1.Sz]);
        Result result = x.TryDecodeRaw(inputData[..Eip2537.LenG1].Span);

        if (!result)
            return result.Error!;

        if (!(Eip2537.DisableSubgroupChecks || x.InGroup()))
            return Errors.G1PointSubgroup;

        // multiplying by zero gives infinity point
        // any scalar multiplied by infinity point is infinity point
        bool scalarIsZero = !inputData.Span[Eip2537.LenG1..].ContainsAnyExcept((byte)0);

        if (scalarIsZero || x.IsInf())
            return Eip2537.G1Infinity;

        Span<byte> scalar = stackalloc byte[32];
        inputData.Span[Eip2537.LenG1..].CopyTo(scalar);
        scalar.Reverse();

        G1 res = x.Mult(scalar);
        return res.EncodeRaw();
    }

    private Result<byte[]> Msm(ReadOnlyMemory<byte> inputData, int nItems)
    {
        // rented without zero-init: every slot MultiMultAffine reads is written during decode below
        using ArrayPoolSpan<long> rawPoints = new(nItems * G1Affine.Sz);
        using ArrayPoolSpan<byte> rawScalars = new(nItems * 32);
        using ArrayPoolList<int> pointDestinations = new(nItems);

        // calculate where in rawPoints buffer decoded points should go
        int npoints = 0;
        for (int i = 0; i < nItems; i++)
        {
            int offset = i * ItemSize;
            ReadOnlySpan<byte> rawPoint = inputData[offset..(offset + Eip2537.LenG1)].Span;

            // exclude infinity points
            int dest = rawPoint.ContainsAnyExcept((byte)0) ? npoints++ : -1;
            pointDestinations.Add(dest);
        }

        // only infinity points so return infinity
        if (npoints == 0)
        {
            return Eip2537.G1Infinity;
        }

        Memory<long> rawPointsMemory = rawPoints.AsMemory();
        Memory<byte> rawScalarsMemory = rawScalars.AsMemory();
        // decode points to rawPoints buffer
        // n.b. subgroup checks carried out as part of decoding
        Result result = Eip2537.DecodeAll(pointDestinations.Count,
            index => Eip2537.TryDecodeG1ToBuffer(inputData, rawPointsMemory, rawScalarsMemory, pointDestinations[index], index));

        if (!result)
            return result.Error!;

        // compute res = rawPoints_0 * rawScalars_0 + rawPoints_1 * rawScalars_1 + ...
        G1 res = new G1(stackalloc long[G1.Sz]).MultiMultAffine(rawPoints, rawScalars, npoints);
        return res.EncodeRaw();
    }
}
