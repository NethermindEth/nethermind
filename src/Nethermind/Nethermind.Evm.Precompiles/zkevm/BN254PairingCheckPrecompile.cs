// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Evm.Precompiles;

public partial class BN254PairingCheckPrecompile
{
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        if (!ValidateInputLength(inputData))
            return Errors.InvalidInputLength;

        if (inputData.Length == 0)
        {
            byte[] output = new byte[32];
            output[31] = 1;

            return output;
        }

        Accelerators.Status status = Accelerators.BN254Pairing(
            inputData.Span,
            (nuint)inputData.Length / BN254.PairSize,
            out bool verified
        );

        if (status == Accelerators.Status.OK)
        {
            byte[] output = new byte[32];

            if (verified)
                output[31] = 1;

            return output;
        }

        return Errors.Failed;
    }
}
