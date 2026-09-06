// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Trie
{
    public static partial class Nibbles
    {
        private static readonly ulong[] ExpandMasks = [0x0000FFFF0000FFFFUL, 0x00FF00FF00FF00FFUL, 0x000F000F000F000FUL];

        /// <summary>Expands <paramref name="count"/> bytes into high/low nibble pairs.</summary>
        /// <remarks>
        /// SWAR: four source bytes spread into 16-bit lanes of one word, then split into the two
        /// nibble bytes per lane with shared masks and stored as a single 64-bit write. Byte-wide
        /// stores are among the most expensive memory accesses in the zkVM cost model.
        /// Caller guarantees <paramref name="nibbles"/> holds <c>2 * count</c> bytes.
        /// Little-endian only: the lane order reaches memory as ascending nibbles solely because the
        /// 64-bit store writes the low byte first. riscv64 is little-endian; the host keeps the plain
        /// loop in <c>Nibbles.std.cs</c>, which is endian-neutral.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ExpandNibbles(ref byte bytes, ref byte nibbles, int count)
        {
            // Frozen-array loads rather than literals: the riscv64 backend materializes each 64-bit
            // constant with a five-instruction sequence.
            ref ulong masks = ref MemoryMarshal.GetArrayDataReference(ExpandMasks);
            ulong m16 = masks;
            ulong m8 = Unsafe.Add(ref masks, 1);
            ulong mNibble = Unsafe.Add(ref masks, 2);
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

        /// <summary>Length of the common prefix of two nibble keys.</summary>
        /// <remarks>Word-at-a-time: the BCL scalar fallback compares byte by byte, and no vector
        /// path is available on riscv64. The mismatch position falls out of the XOR's low set bit,
        /// which is the lowest differing address only on a little-endian target.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int CommonPrefixLength(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            int length = Math.Min(left.Length, right.Length);
            ref byte l = ref MemoryMarshal.GetReference(left);
            ref byte r = ref MemoryMarshal.GetReference(right);
            int i = 0;
            for (; i + sizeof(ulong) <= length; i += sizeof(ulong))
            {
                ulong diff = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref l, i)) ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r, i));
                if (diff != 0)
                {
                    return i + (BitOperations.TrailingZeroCount(diff) >> 3);
                }
            }

            for (; i < length; i++)
            {
                if (Unsafe.Add(ref l, i) != Unsafe.Add(ref r, i)) break;
            }

            return i;
        }
    }
}
