// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class Blake2FPrecompile
{
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        if (!TryPrepareInput(inputData, out ReadOnlySpan<byte> inputSpan, out Result<byte[]> error))
            return error;

        byte[] result = GC.AllocateUninitializedArray<byte>(64);

        inputSpan.Slice(4, 64).CopyTo(result);

        Span<ulong> state = MemoryMarshal.Cast<byte, ulong>(result.AsSpan());
        ReadOnlySpan<ulong> message = MemoryMarshal.Cast<byte, ulong>(inputSpan.Slice(68, 128));
        ReadOnlySpan<ulong> offset = MemoryMarshal.Cast<byte, ulong>(inputSpan.Slice(196, 16));

        ZiskBindings.Crypto.blake2b_compress_c(
            BinaryPrimitives.ReadUInt32BigEndian(inputSpan),
            state,
            message,
            offset,
            inputSpan[212]
        );

        return result;
    }
}
