// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Core.Extensions;

public static partial class EvmWordExtensions
{
    // RISC-V has no byte-swap instruction, so the BCL expands to a byte-at-a-time shuffle;
    // Bswap64 does it with three masked shift/or pairs on whole words.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReverseBytes(ulong value) => ZkEvmBitOperations.Bswap64(value);
}
