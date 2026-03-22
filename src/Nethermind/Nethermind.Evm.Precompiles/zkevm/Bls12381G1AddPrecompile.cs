// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class Bls12381G1AddPrecompile
{
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        if (!ValidateInputLength(inputData))
            return Errors.InvalidInputLength;

        Span<byte> decoded = stackalloc byte[Eip2537.LenG1Trimmed * 2];

        if (!Eip2537.TryDecodeG1(inputData.Span[..Eip2537.LenG1], decoded[..Eip2537.LenG1Trimmed]))
            return Errors.InvalidFieldElementTopBytes;

        if (!Eip2537.TryDecodeG1(inputData.Span[Eip2537.LenG1..], decoded[Eip2537.LenG1Trimmed..]))
            return Errors.InvalidFieldElementTopBytes;

        Span<byte> output = stackalloc byte[Eip2537.LenG1Trimmed];

        // TODO: consider matching error codes with std implementation

        byte status = ZiskBindings.Crypto.bls12_381_g1_add_c(
            output, decoded[..Eip2537.LenG1Trimmed], decoded[Eip2537.LenG1Trimmed..]);

        if (status <= 1)
        {
            byte[] encoded = new byte[Eip2537.LenG1];

            if (status == 0)
                Eip2537.EncodeG1(output, encoded);

            return encoded;
        }

        return Errors.Failed;
    }
}
