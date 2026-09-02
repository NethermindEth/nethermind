// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Extensions;

public static unsafe partial class Bytes
{
    /// <summary>
    /// Reverses the byte order of a 64-bit word.
    /// </summary>
    /// <remarks>
    /// Named for its width rather than after <see cref="BinaryPrimitives.ReverseEndianness(ulong)"/>:
    /// narrower unsigned arguments widen implicitly, so a name shared with the BCL's twelve overloads
    /// would silently swap an 8-byte zero-extension. Exists as a std/zkevm pair because the fastest
    /// form differs per target; use it wherever a hot path swaps whole words so the guest build picks
    /// up its variant.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Bswap64(ulong value) => BinaryPrimitives.ReverseEndianness(value);
}
