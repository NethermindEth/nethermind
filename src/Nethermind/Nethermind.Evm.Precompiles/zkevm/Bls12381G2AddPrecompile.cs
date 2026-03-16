// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class Bls12381G2AddPrecompile
{
    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (!ValidateInputLength(inputData))
            return Errors.InvalidInputLength;

        Span<byte> input = stackalloc byte[Eip2537.LenG2Trimmed * 2];

        if (!Eip2537.TryDecodeG2(inputData.Span[..Eip2537.LenG2], input[..Eip2537.LenG2Trimmed]))
            return Errors.InvalidFieldElementTopBytes;

        if (!Eip2537.TryDecodeG2(inputData.Span[Eip2537.LenG2..], input[Eip2537.LenG2Trimmed..]))
            return Errors.InvalidFieldElementTopBytes;

        Span<byte> output = stackalloc byte[Eip2537.LenG2Trimmed];

        // TODO: consider matching error codes with std implementation

        byte status = ZiskBindings.Crypto.bls12_381_g2_add_c(
            output, input[..Eip2537.LenG2Trimmed], input[Eip2537.LenG2Trimmed..]);

        if (status <= 1)
        {
            byte[] outputData = new byte[Eip2537.LenG2];

            if (status == 0)
                Eip2537.EncodeG2(output, outputData);

            return outputData;
        }

        return Errors.Failed;
    }
}
