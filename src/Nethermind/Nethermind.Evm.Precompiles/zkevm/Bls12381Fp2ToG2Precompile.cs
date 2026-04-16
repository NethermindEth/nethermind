// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class Bls12381Fp2ToG2Precompile
{
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        if (!ValidateInputLength(inputData))
            return Errors.InvalidInputLength;

        Span<byte> decoded = stackalloc byte[Eip2537.LenFpTrimmed * 2];

        if (!Eip2537.TryDecodeFp2(inputData.Span, decoded))
            return Errors.InvalidFieldElementTopBytes;

        Span<byte> output = stackalloc byte[Eip2537.LenG2Trimmed];

        byte status = ZiskBindings.Crypto.bls12_381_fp2_to_g2_c(output, decoded);

        if (status == 0)
        {
            byte[] encoded = new byte[Eip2537.LenG2];

            Eip2537.EncodeG2(output, encoded);

            return encoded;
        }

        return Errors.Failed;
    }
}
