// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using G2 = Nethermind.Crypto.Bls.P2;
using G2Affine = Nethermind.Crypto.Bls.P2Affine;

namespace Nethermind.Evm.Precompiles;

public partial class Bls12381G2MsmPrecompile
{
    [SkipLocalsInit]
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        Metrics.Bls12381G2MsmPrecompile++;

        if (!ValidateInputLength(inputData))
            return Errors.InvalidInputLength;

        // use Mul to optimize single point multiplication
        int nItems = inputData.Length / ItemSize;
        return nItems == 1 ? Mul(inputData) : Msm(inputData, nItems);
    }

    private Result<byte[]> Mul(ReadOnlyMemory<byte> inputData)
    {
        G2 x = new(stackalloc long[G2.Sz]);
        Result result = x.TryDecodeRaw(inputData[..Eip2537.LenG2].Span);

        if (!result)
            return result.Error!;

        if (!(Eip2537.DisableSubgroupChecks || x.InGroup()))
            return Errors.G2PointSubgroup;

        // multiplying by zero gives infinity point
        // any scalar multiplied by infinity point is infinity point
        bool scalarIsZero = !inputData[Eip2537.LenG2..].Span.ContainsAnyExcept((byte)0);

        if (scalarIsZero || x.IsInf())
            return Eip2537.G2Infinity;

        Span<byte> scalar = stackalloc byte[32];
        inputData.Span[Eip2537.LenG2..].CopyTo(scalar);
        scalar.Reverse();

        G2 res = x.Mult(scalar);
        return res.EncodeRaw();
    }

    private Result<byte[]> Msm(ReadOnlyMemory<byte> inputData, int nItems)
    {
        // clearFirst: false — every slot MultiMultAffine reads is written during decode below
        using ArrayPoolList<long> pointBuffer = new(SafeArrayPool<long>.Shared, nItems * G2Affine.Sz, nItems * G2Affine.Sz, clearFirst: false);
        using ArrayPoolList<byte> scalarBuffer = new(SafeArrayPool<byte>.Shared, nItems * 32, nItems * 32, clearFirst: false);
        using ArrayPoolList<int> pointDestinations = new(nItems);

        // calculate where in rawPoints buffer decoded points should go
        int npoints = 0;
        for (int i = 0; i < nItems; i++)
        {
            int offset = i * ItemSize;
            ReadOnlySpan<byte> rawPoint = inputData[offset..(offset + Eip2537.LenG2)].Span;

            // exclude infinity points
            int dest = rawPoint.ContainsAnyExcept((byte)0) ? npoints++ : -1;
            pointDestinations.Add(dest);
        }

        // only infinity points so return infinity
        if (npoints == 0)
            return Eip2537.G2Infinity;

        Memory<long> pointMemory = pointBuffer.AsMemory();
        Memory<byte> scalarMemory = scalarBuffer.AsMemory();
        // decode points to rawPoints buffer
        // n.b. subgroup checks carried out as part of decoding
        Result result = Eip2537.DecodeAll(pointDestinations.Count,
            index => Eip2537.TryDecodeG2ToBuffer(inputData, pointMemory, scalarMemory, pointDestinations[index], index));

        if (!result)
            return result.Error!;

        // compute res = rawPoints_0 * rawScalars_0 + rawPoints_1 * rawScalars_1 + ...
        G2 res = new G2(stackalloc long[G2.Sz]).MultiMultAffine(pointBuffer.AsSpan(), scalarBuffer.AsSpan(), npoints);
        return res.EncodeRaw();
    }
}
