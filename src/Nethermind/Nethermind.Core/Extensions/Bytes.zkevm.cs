// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Core.Extensions;

public static unsafe partial class Bytes
{
    /// <inheritdoc cref="ReverseEndianness(ulong)"/>
    /// <remarks>
    /// RISC-V has no byte-swap instruction, so the BCL's <c>ReverseEndianness</c> expands to a
    /// byte-at-a-time shuffle; <see cref="ZkEvmBitOperations.Bswap64"/> does it with three masked
    /// shift/or pairs on whole words.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReverseEndianness(ulong value) => ZkEvmBitOperations.Bswap64(value);
}
