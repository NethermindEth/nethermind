// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Trie
{
    public static partial class Nibbles
    {
        /// <summary>Expands <paramref name="count"/> bytes into high/low nibble pairs.</summary>
        /// <remarks>
        /// SWAR: four source bytes spread into 16-bit lanes of one word, then split into the two
        /// nibble bytes per lane with shared masks and stored as a single 64-bit write. Byte-wide
        /// stores are among the most expensive memory accesses in the zkVM cost model.
        /// Caller guarantees <paramref name="nibbles"/> holds <c>2 * count</c> bytes.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ExpandNibbles(ref byte bytes, ref byte nibbles, int count)
        {
            ulong m16 = 0x0000FFFF0000FFFFUL;
            ulong m8 = 0x00FF00FF00FF00FFUL;
            ulong mNibble = 0x000F000F000F000FUL;
            int i = 0;
            for (; i + sizeof(uint) <= count; i += sizeof(uint))
            {
                ulong v = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref bytes, i));
                v = (v | (v << 16)) & m16;
                v = (v | (v << 8)) & m8;
                ulong expanded = ((v >> 4) & mNibble) | ((v & mNibble) << 8);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref nibbles, i * 2), expanded);
            }

            for (; i < count; i++)
            {
                int value = Unsafe.Add(ref bytes, i);
                Unsafe.Add(ref nibbles, i * 2) = (byte)(value >> 4);
                Unsafe.Add(ref nibbles, i * 2 + 1) = (byte)(value & 15);
            }
        }
    }
}
