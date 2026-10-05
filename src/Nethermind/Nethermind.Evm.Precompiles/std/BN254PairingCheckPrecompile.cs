// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class BN254PairingCheckPrecompile
{
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        Metrics.Bn254PairingPrecompile++;

        if (!ValidateInputLength(inputData))
            return Errors.Bn254PairingInputLength;

        byte[] output = new byte[32];

        string? error = BN254.CheckPairing(output, inputData.Span);
        return error is null ? output : error;
    }
}
