// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.State.Flat.Hsst;

internal static class HsstHash
{
    /// <summary>
    /// 32-bit hash used by <see cref="IndexType.BTreeHashIndex"/> and the in-leaf hash
    /// probe for slot computation. Builder and reader must agree byte-for-byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint HashKey(scoped ReadOnlySpan<byte> key) =>
        (uint)XxHash3.HashToUInt64(key);

    /// <summary>
    /// Smallest power-of-two bucket count satisfying load factor ≤
    /// <paramref name="targetUtilization"/> for <paramref name="entryCount"/> entries.
    /// Equivalent to <c>2^ceil(log2(ceil(N / target)))</c>, with a floor of 1.
    /// Shared by the file-level hash index and the in-leaf hash probe so writer and
    /// reader agree byte-for-byte.
    /// </summary>
    public static int BucketCount(int entryCount, double targetUtilization = 0.75)
    {
        long required = (long)Math.Ceiling(entryCount / targetUtilization);
        if (required < 1) required = 1;
        int log2 = required <= 1 ? 0 : (32 - BitOperations.LeadingZeroCount((uint)(required - 1)));
        if (log2 > 31) throw new InvalidOperationException("Hash index table size too large.");
        return 1 << log2;
    }
}
