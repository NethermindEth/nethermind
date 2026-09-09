// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using G1 = Nethermind.Crypto.Bls.P1;

namespace Nethermind.Evm.Precompiles;

public partial class Bls12381FpToG1Precompile
{
    [SkipLocalsInit]
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        Metrics.Bls12381FpToG1Precompile++;

        if (!ValidateInputLength(inputData))
            return Errors.InvalidInputLength;

        G1 res = new(stackalloc long[G1.Sz]);
        Result result = Eip2537.ValidRawFp(inputData.Span);

        if (!result)
            return result.Error!;

        // map field point to G1
        ReadOnlySpan<byte> fp = inputData[Eip2537.LenFpPad..Eip2537.LenFp].Span;
        res.MapTo(fp);

        return res.EncodeRaw();
    }
}
