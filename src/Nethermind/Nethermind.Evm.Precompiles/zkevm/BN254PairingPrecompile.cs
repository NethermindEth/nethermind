// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

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
            byte result = ZiskBindings.Crypto.bn254_pairing_check_c(
                inputData.Span,
                (nuint)inputData.Length / BN254.PairSize
            );
            success = result <= 1;

            if (result == 0)
                output[31] = 1;
        }

        return success ? output : Errors.Failed;
    }
}
