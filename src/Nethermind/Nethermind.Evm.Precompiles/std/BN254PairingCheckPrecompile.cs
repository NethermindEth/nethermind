// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class BN254PairingCheckPrecompile
{
    private static readonly byte[] _successResult = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1];

    public static Address Address { get; } = Address.FromNumber(8);

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        if (!ValidateInputLength(inputData))
            return Errors.Bn254PairingInputLength;

        string? error = BN254.CheckPairing(inputData.Span, out bool isOne);
        return error is null ? (isOne ? _successResult : Bytes.Zero32) : error;
    }
}
