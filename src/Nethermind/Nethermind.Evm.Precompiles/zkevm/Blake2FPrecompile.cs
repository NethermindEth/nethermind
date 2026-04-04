// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
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

        Span<ulong> state = stackalloc ulong[8];
        Span<ulong> message = stackalloc ulong[16];
        Span<ulong> offset = stackalloc ulong[2];

        ref byte inputSrc = ref MemoryMarshal.GetReference(inputSpan);
        ref byte stateDst = ref Unsafe.As<ulong, byte>(ref MemoryMarshal.GetReference(state));
        ref byte messageDst = ref Unsafe.As<ulong, byte>(ref MemoryMarshal.GetReference(message));
        ref byte offsetDst = ref Unsafe.As<ulong, byte>(ref MemoryMarshal.GetReference(offset));

        Unsafe.CopyBlockUnaligned(ref stateDst, ref Unsafe.Add(ref inputSrc, 4), 64);
        Unsafe.CopyBlockUnaligned(ref messageDst, ref Unsafe.Add(ref inputSrc, 68), 128);
        Unsafe.CopyBlockUnaligned(ref offsetDst, ref Unsafe.Add(ref inputSrc, 196), 16);

        ZiskBindings.Crypto.blake2b_compress_c(
            BinaryPrimitives.ReadUInt32BigEndian(inputSpan),
            state,
            message,
            offset,
            inputSpan[212]
        );

        Unsafe.CopyBlockUnaligned(ref MemoryMarshal.GetReference(result), ref stateDst, (uint)result.Length);

        return result;
    }
}
