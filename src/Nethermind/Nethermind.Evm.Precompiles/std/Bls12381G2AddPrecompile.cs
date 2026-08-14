// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using G2 = Nethermind.Crypto.Bls.P2;

namespace Nethermind.Evm.Precompiles;

public partial class Bls12381G2AddPrecompile
{
    [SkipLocalsInit]
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        Metrics.Bls12381G2AddPrecompile++;

        if (!ValidateInputLength(inputData))
            return Errors.InvalidInputLength;

        G2 x = new(stackalloc long[G2.Sz]);
        G2 y = new(stackalloc long[G2.Sz]);
        Result result =
            x.TryDecodeRaw(inputData[..Eip2537.LenG2].Span) &&
            y.TryDecodeRaw(inputData[Eip2537.LenG2..].Span);

        if (result)
        {
            // adding to infinity point has no effect
            if (x.IsInf())
                return inputData[Eip2537.LenG2..].ToArray();

            if (y.IsInf())
                return inputData[..Eip2537.LenG2].ToArray();

            G2 res = x.Add(y);
            return res.EncodeRaw();
        }

        return result.Error!;
    }
}
