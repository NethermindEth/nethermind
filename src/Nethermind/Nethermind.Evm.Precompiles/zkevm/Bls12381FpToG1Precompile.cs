// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class Bls12381FpToG1Precompile
{
    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (!ValidateInputLength(inputData))
            return Errors.InvalidInputLength;

        Span<byte> input = stackalloc byte[Eip2537.LenFpTrimmed];

        if (!Eip2537.TryDecodeFp(inputData.Span, input))
            return Errors.InvalidFieldElementTopBytes;

        Span<byte> output = stackalloc byte[Eip2537.LenG1Trimmed];

        byte status = ZiskBindings.Crypto.bls12_381_fp_to_g1_c(output, input);

        if (status == 0)
        {
            byte[] outputData = new byte[Eip2537.LenG1];

            Eip2537.EncodeG1(output, outputData);

            return outputData;
        }

        return Errors.Failed;
    }
}
