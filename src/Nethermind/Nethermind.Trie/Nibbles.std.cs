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
            // We use Unsafe here as we have verified all the bounds above and also only go to length
            // However the loop doesn't start a 0 and the nibbles span access is complex (rather than just i)
            // so the Jit can't work out if the bounds checks and their if+exceptions can be eliminated.
            // Because of this using regular array style access causes 3 bounds checks to be inserted.
            for (int i = 0; i < count; i++)
            {
                int value = Unsafe.Add(ref bytes, i);
                Unsafe.Add(ref nibbles, i * 2) = (byte)(value >> 4);
                Unsafe.Add(ref nibbles, i * 2 + 1) = (byte)(value & 15);
            }
        }

        /// <summary>Length of the common prefix of two nibble keys.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int CommonPrefixLength(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
            => left.CommonPrefixLength(right);
    }
}
