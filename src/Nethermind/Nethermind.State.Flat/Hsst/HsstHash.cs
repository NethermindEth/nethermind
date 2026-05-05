// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Hashing;
using System.Runtime.CompilerServices;

namespace Nethermind.State.Flat.Hsst;

internal static class HsstHash
{
    /// <summary>
    /// 32-bit hash used by <see cref="IndexType.BTreeHashIndex"/> for slot computation.
    /// Builder and reader must agree byte-for-byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint HashKey(scoped ReadOnlySpan<byte> key) =>
        (uint)XxHash3.HashToUInt64(key);

    /// <summary>
    /// Bucket count for a hash table holding <paramref name="entryCount"/> entries at the
    /// given target load factor. With Lemire's multiply-shift reduction the table is no
    /// longer constrained to a power of two, so we size it directly to
    /// <c>max(1, ceil(n / target))</c>. Shared by every site that builds or reads a hash
    /// section so writer and reader agree.
    /// </summary>
    public static int BucketCount(int entryCount, double targetUtilization = 0.75)
    {
        long required = (long)Math.Ceiling(entryCount / targetUtilization);
        if (required < 1) required = 1;
        if (required > int.MaxValue) throw new InvalidOperationException("Hash index table size too large.");
        return (int)required;
    }

    /// <summary>
    /// Lemire's fast reduction: maps a 32-bit hash uniformly into <c>[0, tableSize)</c>
    /// without requiring <paramref name="tableSize"/> to be a power of two. See
    /// <see href="https://lemire.me/blog/2016/06/27/a-fast-alternative-to-the-modulo-reduction/"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Slot(uint hash, int tableSize) =>
        (uint)(((ulong)hash * (ulong)(uint)tableSize) >> 32);
}
