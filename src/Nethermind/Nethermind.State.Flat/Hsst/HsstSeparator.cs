// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.State.Flat.Hsst;

internal static class HsstSeparator
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSeparatorLength(ReadOnlySpan<byte> prevKey, ReadOnlySpan<byte> currKey, ReadOnlySpan<byte> nextKey, int minSeparatorLength = 0)
    {
        int minVsPrev = 0;
        if (!prevKey.IsEmpty)
        {
            int common = CommonPrefixLength(prevKey, currKey);
            minVsPrev = common + 1;
        }

        int minVsNext = 0;
        if (!nextKey.IsEmpty)
        {
            int common = CommonPrefixLength(currKey, nextKey);
            minVsNext = common + 1;
        }

        int len = Math.Max(minVsPrev, minVsNext);
        len = Math.Min(len, currKey.Length);
        if (len == 0) len = Math.Min(1, currKey.Length);

        return Math.Min(Math.Max(len, minSeparatorLength), currKey.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CommonPrefixLength(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int minLen = Math.Min(a.Length, b.Length);
        for (int i = 0; i < minLen; i++)
        {
            if (a[i] != b[i]) return i;
        }
        return minLen;
    }
}
