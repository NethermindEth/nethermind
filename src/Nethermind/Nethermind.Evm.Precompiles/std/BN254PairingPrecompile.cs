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
        Metrics.Bn254PairingPrecompile++;

        if (!ValidateInputLength(inputData, releaseSpec))
            return Errors.InvalidInputLength;

        byte[] output = new byte[32];

        return BN254.CheckPairing(output, inputData.Span) ? output : Errors.Failed;
    }
}
