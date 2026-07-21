// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Zkvm.Abstractions;

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

        Accelerators.Status status = Accelerators.Bls12381G1Add(
            decoded[..Eip2537.LenG1Trimmed], decoded[Eip2537.LenG1Trimmed..], output);

        if (status == Accelerators.Status.OK)
        {
            byte[] encoded = new byte[Eip2537.LenG1];

            Eip2537.EncodeG1(output, encoded);

            return encoded;
        }

        return Errors.Failed;
    }
}
