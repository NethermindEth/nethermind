// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class SecP256r1Precompile
{
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        ReadOnlySpan<byte> input = inputData.Span;

        return inputData.Length == 160 && ZiskBindings.Crypto.secp256r1_ecdsa_verify_c(
            input[..32], input[32..96], input[96..]
            ) ? _successResult : [];
    }
}
