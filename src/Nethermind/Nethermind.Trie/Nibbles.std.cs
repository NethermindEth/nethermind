// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Trie
{
    public static partial class Nibbles
    {
        /// <summary>Expands <paramref name="count"/> bytes into high/low nibble pairs.</summary>
        /// <remarks>Caller guarantees <paramref name="nibbles"/> holds <c>2 * count</c> bytes.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ExpandNibbles(ref byte bytes, ref byte nibbles, int count)
        {
            // Raw refs rather than spans: the doubled destination index defeats the JIT's bounds-check
            // elimination, so span access would cost three bounds checks per iteration.
            for (int i = 0; i < count; i++)
            {
                int value = Unsafe.Add(ref bytes, i);
                Unsafe.Add(ref nibbles, i * 2) = (byte)(value >> 4);
                Unsafe.Add(ref nibbles, i * 2 + 1) = (byte)(value & 15);
            }
        }

        /// <summary>Packs <c>2 * count</c> nibble bytes, high nibble first, into <paramref name="count"/> bytes.</summary>
        /// <remarks>Caller guarantees <paramref name="nibbles"/> holds <c>2 * count</c> bytes, each in
        /// <c>0..15</c>, and that <paramref name="bytes"/> has room for <paramref name="count"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void PackNibbles(ref byte nibbles, ref byte bytes, int count)
        {
            // Raw refs rather than spans, as in ExpandNibbles: the doubled source index defeats the
            // JIT's bounds-check elimination.
            for (int i = 0; i < count; i++)
            {
                Unsafe.Add(ref bytes, i) =
                    (byte)((Unsafe.Add(ref nibbles, i * 2) << 4) | Unsafe.Add(ref nibbles, i * 2 + 1));
            }
        }

        /// <summary>Length of the common prefix of two nibble keys.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int CommonPrefixLength(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
            => left.CommonPrefixLength(right);
    }
}
