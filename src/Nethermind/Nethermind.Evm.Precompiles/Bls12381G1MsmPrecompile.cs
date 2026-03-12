// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using G1 = Nethermind.Crypto.Bls.P1;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// <see href="https://eips.ethereum.org/EIPS/eip-2537" />
/// </summary>
public class Bls12381G1MsmPrecompile : IPrecompile<Bls12381G1MsmPrecompile>
{
    public static readonly Bls12381G1MsmPrecompile Instance = new();

    private Bls12381G1MsmPrecompile() { }

    public static Address Address { get; } = Address.FromNumber(0x0c);

    public static string Name => "BLS12_G1MSM";

    public long BaseGasCost(IReleaseSpec releaseSpec) => 0L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        int k = inputData.Length / ItemSize;
        return 12000L * k * Eip2537.DiscountForG1(k) / 1000;
    }

    public const int ItemSize = 160;

    [SkipLocalsInit]
    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.Bls12381G1MsmPrecompile++;

        if (inputData.Length % ItemSize > 0 || inputData.Length == 0)
        {
            return Errors.InvalidInputLength;
        }

        // use Mul to optimize single point multiplication
        int nItems = inputData.Length / ItemSize;
        return nItems == 1 ? Mul(inputData) : MSM(inputData, nItems);
    }

    private Result<byte[]> Mul(ReadOnlyMemory<byte> inputData)
    {
        G1 x = new(stackalloc long[G1.Sz]);
        Result result = x.TryDecodeRaw(inputData[..Eip2537.LenG1].Span);
        if (!result) return result.Error!;

        if (!(Eip2537.DisableSubgroupChecks || x.InGroup())) return Errors.G1PointSubgroup;

        // multiplying by zero gives infinity point
        // any scalar multiplied by infinity point is infinity point
        bool scalarIsZero = !inputData.Span[Eip2537.LenG1..].ContainsAnyExcept((byte)0);
        if (scalarIsZero || x.IsInf())
        {
            return Eip2537.G1Infinity;
        }

        Span<byte> scalar = stackalloc byte[32];
        inputData.Span[Eip2537.LenG1..].CopyTo(scalar);
        scalar.Reverse();

        G1 res = x.Mult(scalar);
        return res.EncodeRaw();
    }

    private Result<byte[]> MSM(ReadOnlyMemory<byte> inputData, int nItems)
    {
        using ArrayPoolList<long> rawPoints = new(nItems * G1.Sz, nItems * G1.Sz);
        using ArrayPoolList<byte> rawScalars = new(nItems * 32, nItems * 32);
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

        Result result = Result.Success;

        // decode points to rawPoints buffer
        // n.b. subgroup checks carried out as part of decoding
#pragma warning disable CS0162 // Unreachable code detected
        if (Eip2537.DisableConcurrency)
        {
            for (int i = 0; i < pointDestinations.Count && result; i++)
            {
                result = Eip2537.TryDecodeG1ToBuffer(inputData, rawPoints.AsMemory(), rawScalars.AsMemory(), pointDestinations[i], i);
            }
        }
        else
        {
            Parallel.ForEach(pointDestinations, (dest, state, i) =>
            {
                int index = (int)i;
                Result local = Eip2537.TryDecodeG1ToBuffer(inputData, rawPoints.AsMemory(), rawScalars.AsMemory(), dest, index);
                if (!local)
                {
                    result = local;
                    state.Break();
                }
            });
        }
#pragma warning restore CS0162 // Unreachable code detected

        if (!result) return result.Error!;

        // compute res = rawPoints_0 * rawScalars_0 + rawPoints_1 * rawScalars_1 + ...
        G1 res = new G1(stackalloc long[G1.Sz]).MultiMult(rawPoints.AsSpan(), rawScalars.AsSpan(), npoints);
        return res.EncodeRaw();
    }
}
