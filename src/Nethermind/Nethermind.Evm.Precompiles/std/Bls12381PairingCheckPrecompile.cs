// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using G1 = Nethermind.Crypto.Bls.P1;
using G2 = Nethermind.Crypto.Bls.P2;
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

        G1 x = new(stackalloc long[G1.Sz]);
        G2 y = new(stackalloc long[G2.Sz]);

        using ArrayPoolListRef<long> buf = new(GT.Sz * 2, GT.Sz * 2);
        var acc = GT.One(buf.AsSpan());
        GT p = new(buf.AsSpan()[GT.Sz..]);

        for (int i = 0; i < inputData.Length / PairSize; i++)
        {
            int offset = i * PairSize;

            Result result =
                x.TryDecodeRaw(inputData[offset..(offset + Eip2537.LenG1)].Span) &&
                y.TryDecodeRaw(inputData[(offset + Eip2537.LenG1)..(offset + PairSize)].Span);

            if (result)
            {
                if (!(Eip2537.DisableSubgroupChecks || x.InGroup()))
                    return Errors.G1PointSubgroup;

                if (!(Eip2537.DisableSubgroupChecks || y.InGroup()))
                    return Errors.G2PointSubgroup;

                // x == inf || y == inf -> e(x, y) = 1
                if (x.IsInf() || y.IsInf())
                    continue;

                // acc *= e(x, y)
                p.MillerLoop(y, x);
                acc.Mul(p);
            }
            else
            {
                return result.Error!;
            }
        }

        // e(x_0, y_0) * e(x_1, y_1) * ... == 1
        byte[] res = new byte[32];

        if (acc.FinalExp().IsOne()) res[31] = 1;

        return res;
    }
}
