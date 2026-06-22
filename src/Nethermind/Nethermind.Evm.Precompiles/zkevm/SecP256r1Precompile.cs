// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Evm.Precompiles;

public partial class SecP256r1Precompile
{
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        ReadOnlySpan<byte> input = inputData.Span;

        return input.Length == 160 && Accelerators.SecP256r1Verify(
            input[..32], input[32..96], input[96..]
            ) ? _successResult : [];
    }
}
