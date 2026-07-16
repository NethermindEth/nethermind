// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;
using G2Affine = Nethermind.Crypto.Bls.P2Affine;
using GT = Nethermind.Crypto.Bls.PT;

namespace Nethermind.Evm.Precompiles;

public partial class Bls12381PairingCheckPrecompile
{
    [SkipLocalsInit]
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        Metrics.Bls12381PairingCheckPrecompile++;

        if (!ValidateInputLength(inputData))
            return Errors.InvalidInputLength;

        int nItems = inputData.Length / PairSize;

        // Scratch buffers rented without zero-init (ArrayPoolSpan does not clear on rent): MillerLoopN
        // only reads the first npairs slots of each buffer, and every one of those slots is fully
        // written by TryDecodePairToBuffer before MillerLoopN runs, so a clear is wasted.
        using ArrayPoolSpan<long> g1Points = new(nItems * G1Affine.Sz);
        using ArrayPoolSpan<long> g2Points = new(nItems * G2Affine.Sz);
        using ArrayPoolList<int> pairDestinations = new(nItems);

        // calculate where in the point buffers decoded pairs should go;
        // x == inf || y == inf -> e(x, y) = 1, so such pairs are excluded from the Miller loop,
        // but both points are still validated during decoding below
        int npairs = 0;
        for (int i = 0; i < nItems; i++)
        {
            int offset = i * PairSize;
            ReadOnlySpan<byte> rawG1 = inputData[offset..(offset + Eip2537.LenG1)].Span;
            ReadOnlySpan<byte> rawG2 = inputData[(offset + Eip2537.LenG1)..(offset + PairSize)].Span;

            int dest = rawG1.ContainsAnyExcept((byte)0) && rawG2.ContainsAnyExcept((byte)0) ? npairs++ : -1;
            pairDestinations.Add(dest);
        }

        Result result = Result.Success;

        // decode pairs to point buffers
        // n.b. on-curve and subgroup checks carried out as part of decoding
#pragma warning disable CS0162 // Unreachable code detected
        if (Eip2537.DisableConcurrency)
        {
            for (int i = 0; i < pairDestinations.Count && result; i++)
            {
                result = TryDecodePairToBuffer(inputData, g1Points.AsMemory(), g2Points.AsMemory(), pairDestinations[i], i);
            }
        }
        else
        {
            Memory<long> g1Memory = g1Points.AsMemory();
            Memory<long> g2Memory = g2Points.AsMemory();
            Parallel.For(0, pairDestinations.Count, (index, state) =>
            {
                Result local = TryDecodePairToBuffer(inputData, g1Memory, g2Memory, pairDestinations[index], index);
                if (!local)
                {
                    // racy across workers, but every writer stores a failure and only the atomically
                    // written ResultType is read below, so post-barrier result is a failure iff any pair failed
                    result = local;
                    state.Break();
                }
            });
        }
#pragma warning restore CS0162 // Unreachable code detected

        if (!result)
            return result.Error!;

        // every pair contained an infinity point, so the product is the empty product, one
        if (npairs == 0)
        {
            byte[] one = new byte[32];
            one[31] = 1;
            return one;
        }

        // acc = e(x_0, y_0) * e(x_1, y_1) * ... computed in one batched Miller loop
        GT acc = new(stackalloc long[GT.Sz]);
        acc.MillerLoopN(g2Points, g1Points, npairs);

        // e(x_0, y_0) * e(x_1, y_1) * ... == 1
        byte[] res = new byte[32];

        if (acc.FinalExp().IsOne()) res[31] = 1;

        return res;
    }

    private static Result TryDecodePairToBuffer(
        ReadOnlyMemory<byte> inputData,
        Memory<long> g1Buffer,
        Memory<long> g2Buffer,
        int dest,
        int index)
    {
        int offset = index * PairSize;

        // pairs containing a point at infinity (dest == -1) are excluded from the Miller loop,
        // but both encodings must still be validated and subgroup-checked, so decode to scratch
        G1Affine x = new(dest == -1 ? stackalloc long[G1Affine.Sz] : g1Buffer.Span[(dest * G1Affine.Sz)..]);
        G2Affine y = new(dest == -1 ? stackalloc long[G2Affine.Sz] : g2Buffer.Span[(dest * G2Affine.Sz)..]);

        Result result =
            x.TryDecodeRaw(inputData[offset..(offset + Eip2537.LenG1)].Span) &&
            y.TryDecodeRaw(inputData[(offset + Eip2537.LenG1)..(offset + PairSize)].Span);

        if (!result)
            return result;

        if (!(Eip2537.DisableSubgroupChecks || x.InGroup()))
            return Errors.G1PointSubgroup;

        if (!(Eip2537.DisableSubgroupChecks || y.InGroup()))
            return Errors.G2PointSubgroup;

        return Result.Success;
    }
}
