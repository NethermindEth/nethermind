// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Extensions;

public static partial class Bytes
{
    /// <summary>Copies bytes into an array, reusing a shared array for single-byte values.</summary>
    /// <remarks>The returned array must not be modified.</remarks>
    internal static byte[] ToArrayWithSingleByteCache(this ReadOnlySpan<byte> value)
        => value.Length == 1 ? SingleByteArrayCache.Values[value[0]] : value.ToArray();

    private static class SingleByteArrayCache
    {
        internal static readonly byte[][] Values = Create();

        private static byte[][] Create()
        {
            byte[][] values = new byte[256][];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = [(byte)i];
            }
            return values;
        }
    }
}
