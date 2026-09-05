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
        private static readonly ulong[] ExpandMasks = [0x0000FFFF0000FFFFUL, 0x00FF00FF00FF00FFUL, 0x000F000F000F000FUL, 0x00000000FFFFFFFFUL];

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
            ulong mLow = Unsafe.Add(ref masks, 3);
            int i = 0;
            // Eight source bytes per read, expanded as two halves: one load, one loop test and one
            // address computation instead of two, and the four-byte load the zkVM charges roughly eight
            // times an aligned word read becomes a word read. The low half must be masked first - the
            // spread's own mask keeps bits 32..47, which in a whole word hold source bytes four and five.
            for (; i + sizeof(ulong) <= count; i += sizeof(ulong))
            {
                ulong src = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, i));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref nibbles, i * 2), Spread(src & mLow, m16, m8, mNibble));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref nibbles, (i * 2) + sizeof(ulong)), Spread(src >> 32, m16, m8, mNibble));
            }

            for (; i + sizeof(uint) <= count; i += sizeof(uint))
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref nibbles, i * 2),
                    Spread(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref bytes, i)), m16, m8, mNibble));
            }

            for (; i < count; i++)
            {
                int value = Unsafe.Add(ref bytes, i);
                Unsafe.Add(ref nibbles, i * 2) = (byte)(value >> 4);
                Unsafe.Add(ref nibbles, i * 2 + 1) = (byte)(value & 15);
            }
        }

        /// <summary>Packs <c>2 * count</c> nibble bytes, high nibble first, into <paramref name="count"/> bytes.</summary>
        /// <remarks>
        /// SWAR, the inverse of <see cref="ExpandNibbles"/>: eight nibble bytes come in as one word, the
        /// high/low pair of each output byte is folded into the low byte of its 16-bit lane, and the four
        /// lane bytes are gathered into a single 32-bit write. Byte-wide accesses are among the most
        /// expensive memory operations in the zkVM cost model.
        /// Caller guarantees <paramref name="nibbles"/> holds <c>2 * count</c> bytes, each in <c>0..15</c>,
        /// and that <paramref name="bytes"/> has room for <paramref name="count"/>. The range matters more
        /// here than in the scalar form: a wider source byte spills out of its lane and the gather step
        /// then ORs the spill into the neighbouring output byte, where the scalar form truncates locally.
        /// Little-endian only, for the reason given at <see cref="ExpandNibbles"/>; the host keeps the
        /// plain loop in <c>Nibbles.std.cs</c>.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void PackNibbles(ref byte nibbles, ref byte bytes, int count)
        {
            ref ulong masks = ref MemoryMarshal.GetArrayDataReference(ExpandMasks);
            ulong m16 = masks;
            ulong m8 = Unsafe.Add(ref masks, 1);
            int i = 0;
            for (; i + sizeof(uint) <= count; i += sizeof(uint))
            {
                ulong v = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref nibbles, i * 2));
                // Each 16-bit lane ends up holding one output byte: its high nibble shifted up, its low
                // nibble brought down from the next source byte.
                ulong packed = ((v & m8) << 4) | ((v >> 8) & m8);
                // Gather lanes 0..3 into the low four bytes.
                packed = (packed | (packed >> 8)) & m16;
                packed |= packed >> 16;
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref bytes, i), (uint)packed);
            }

            for (; i < count; i++)
            {
                Unsafe.Add(ref bytes, i) =
                    (byte)((Unsafe.Add(ref nibbles, i * 2) << 4) | Unsafe.Add(ref nibbles, i * 2 + 1));
            }
        }

        /// <summary>Spreads the low four bytes of <paramref name="value"/> into eight nibble bytes.</summary>
        /// <remarks>Bits above those four bytes must already be clear: the first mask keeps bits 32..47,
        /// so anything left there would be folded into the third and fourth nibble pair.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Spread(ulong value, ulong m16, ulong m8, ulong mNibble)
        {
            ulong v = (value | (value << 16)) & m16;
            v = (v | (v << 8)) & m8;
            return ((v >> 4) & mNibble) | ((v & mNibble) << 8);
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
