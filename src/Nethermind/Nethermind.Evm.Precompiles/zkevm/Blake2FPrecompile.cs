// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Evm.Precompiles;

public partial class Blake2FPrecompile
{
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        if (!TryPrepareInput(inputData, out ReadOnlySpan<byte> inputSpan, out Result<byte[]> error))
            return error;

        byte[] result = GC.AllocateUninitializedArray<byte>(64);

        inputSpan.Slice(sizeof(uint), result.Length).CopyTo(result);

        Accelerators.Blake2F(
            BinaryPrimitives.ReadUInt32BigEndian(inputSpan),
            result,
            inputSpan.Slice(68, 128),
            inputSpan.Slice(196, 16),
            inputSpan[212]
        );

        return result;
    }
}
