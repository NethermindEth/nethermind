// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Hashing;
using System.Runtime.CompilerServices;

namespace Nethermind.State.Flat.Hsst;

internal static class HsstHash
{
    /// <summary>
    /// 32-bit hash used by <see cref="IndexType.BTreeHashIndex"/> for slot
    /// computation. Builder and reader must agree byte-for-byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint HashKey(scoped ReadOnlySpan<byte> key) =>
        (uint)XxHash3.HashToUInt64(key);
}
