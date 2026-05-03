// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Evm.Precompiles;

public partial class BN254PairingPrecompile
{
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (!ValidateInputLength(inputData, releaseSpec))
            return Errors.InvalidInputLength;

        byte[] output = new byte[32];
        bool success;

        if (inputData.Length == 0)
        {
            output[31] = 1;
            success = true;
        }
        else
        {
            success = Accelerators.BN254Pairing(
                inputData.Span,
                (nuint)inputData.Length / BN254.PairSize,
                out bool verified
            ) == Accelerators.Status.OK;

            if (verified)
                output[31] = 1;
        }

        return success ? output : Errors.Failed;
    }
}
