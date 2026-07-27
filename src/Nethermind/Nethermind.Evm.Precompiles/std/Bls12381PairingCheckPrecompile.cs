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

        // rented without zero-init: every slot MillerLoopN reads is written during decode below
        using ArrayPoolSpan<long> g1Points = new(nItems * G1Affine.Sz);
        using ArrayPoolSpan<long> g2Points = new(nItems * G2Affine.Sz);
        using ArrayPoolList<int> pairDestinations = new(nItems);

        // assign each pair a compacted slot; a pair with an infinity point (e = 1) gets -1 to
        // exclude it from the Miller loop, but is still validated during decode
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

        // decode + on-curve/subgroup-validate each pair into its slot
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
                    // racy but safe: workers only ever store a failure, so post-barrier result fails iff any pair did
                    result = local;
                    state.Break();
                }
            });
        }
#pragma warning restore CS0162 // Unreachable code detected

        if (!result)
            return result.Error!;

        // all pairs had an infinity point: empty product is one
        if (npairs == 0)
        {
            byte[] one = new byte[32];
            one[31] = 1;
            return one;
        }

        // batched product of the per-pair Miller loops
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

        // dest == -1: pair has an infinity point (excluded from the loop) but still fully validated, so decode to scratch
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
