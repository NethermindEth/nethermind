// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Evm.Precompiles;

public partial class Blake2FPrecompile
{
    public static Address Address => field ??= Address.FromNumber(9);
    private static ReadOnlyMemory<byte> InvalidFlagInput => new byte[RequiredInputLength].WithValueAt(212, 0xFF);

    [SkipLocalsInit]
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        if (!TryPrepareInput(inputData, out ReadOnlySpan<byte> inputSpan, out Result<byte[]> error))
            return error;

        // ZisK reads the state, message and offset as 64-bit words and faults unless each is 8-byte
        // aligned, while the input places the message and offset at 4 mod 8. The three are contiguous
        // in the input, so a single copy into a word buffer aligns them all.
        Span<byte> words = MemoryMarshal.AsBytes(stackalloc ulong[(64 + 128 + 16) / sizeof(ulong)]);

        inputSpan.Slice(sizeof(uint), words.Length).CopyTo(words);

        Accelerators.Blake2F(
            BinaryPrimitives.ReadUInt32BigEndian(inputSpan),
            words[..64],
            words.Slice(64, 128),
            words.Slice(192, 16),
            inputSpan[212]
        );

        return words[..64].ToArray();
    }
}
