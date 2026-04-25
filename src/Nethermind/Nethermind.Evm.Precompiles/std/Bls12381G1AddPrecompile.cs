// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using G1 = Nethermind.Crypto.Bls.P1;

namespace Nethermind.Evm.Precompiles;

public partial class Bls12381G1AddPrecompile
{
    [SkipLocalsInit]
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        Metrics.Bls12381G1AddPrecompile++;

        if (!ValidateInputLength(inputData))
            return Errors.InvalidInputLength;

        G1 x = new(stackalloc long[G1.Sz]);
        G1 y = new(stackalloc long[G1.Sz]);
        Result result =
            x.TryDecodeRaw(inputData[..Eip2537.LenG1].Span) &&
            y.TryDecodeRaw(inputData[Eip2537.LenG1..].Span);

        if (result)
        {
            // adding to infinity point has no effect
            if (x.IsInf())
                return inputData[Eip2537.LenG1..].ToArray();

            if (y.IsInf())
                return inputData[..Eip2537.LenG1].ToArray();

            G1 res = x.Add(y);
            return res.EncodeRaw();
        }

        return result.Error!;
    }
}
