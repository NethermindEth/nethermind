// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto.Blake2;

namespace Nethermind.Evm.Precompiles;

public partial class Blake2FPrecompile
{
    private static readonly ReadOnlyMemory<byte> InvalidFlagInput = new byte[RequiredInputLength].WithValueAt(212, 0xFF);
    public static Address Address { get; } = Address.FromNumber(9);

    private readonly Blake2Compression _blake = new();

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        if (!TryPrepareInput(inputData, out ReadOnlySpan<byte> inputSpan, out Result<byte[]> error))
            return error;

        byte[] result = GC.AllocateUninitializedArray<byte>(64);

        _blake.Compress(inputSpan, result);

        return result;
    }
}
