// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Core.Extensions;

public static unsafe partial class Bytes
{
    /// <summary>
    /// Reverses the byte order of a 64-bit word.
    /// </summary>
    /// <remarks>
    /// RISC-V has no byte-swap instruction, so the BCL's <c>ReverseEndianness</c> expands to a
    /// byte-at-a-time shuffle; <see cref="ZkEvmBitOperations.Bswap64"/> does it with three masked
    /// shift/or pairs on whole words. See <c>Bytes.std.cs</c> for the host form.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Bswap64(ulong value) => ZkEvmBitOperations.Bswap64(value);
}
