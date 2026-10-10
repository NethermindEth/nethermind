// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Trie
{
    public static partial class Nibbles
    {
        /// <summary>Expands <paramref name="count"/> bytes into high/low nibble pairs.</summary>
        /// <remarks>Caller guarantees <paramref name="nibbles"/> holds <c>2 * count</c> bytes and does not overlap <paramref name="bytes"/>.</remarks>
        internal static void ExpandNibbles(ref byte bytes, ref byte nibbles, int count)
        {
            Debug.Assert(count >= 0);
            nuint length = (uint)count;
            if (length >= sizeof(uint))
            {
                if (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported)
                {
                    ExpandVectors(ref bytes, ref nibbles, length);
                }
                else
                {
                    ExpandWords(ref bytes, ref nibbles, length);
                }
            }
            else if (length >= sizeof(ushort))
            {
                ExpandPair(ref bytes, ref nibbles, 0);
                if (length > sizeof(ushort))
                {
                    ExpandPair(ref bytes, ref nibbles, 1);
                }
            }
            else if (length != 0)
            {
                int value = bytes;
                nibbles = (byte)(value >> 4);
                Unsafe.Add(ref nibbles, 1) = (byte)(value & 15);
            }
        }

        /// <summary>Packs <c>2 * count</c> nibble bytes, high nibble first, into <paramref name="count"/> bytes.</summary>
        /// <remarks>Caller guarantees <paramref name="nibbles"/> holds <c>2 * count</c> bytes, each in <c>0..15</c>,
        /// and that <paramref name="bytes"/> has room for <paramref name="count"/> and does not overlap <paramref name="nibbles"/>.</remarks>
        internal static void PackNibbles(ref byte nibbles, ref byte bytes, int count)
        {
            Debug.Assert(count >= 0);
            nuint length = (uint)count;
            if (length >= sizeof(uint))
            {
                if (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported)
                {
                    PackVectors(ref nibbles, ref bytes, length);
                }
                else
                {
                    PackWords(ref nibbles, ref bytes, length);
                }
            }
            else if (length >= sizeof(ushort))
            {
                PackPair(ref nibbles, ref bytes, 0);
                if (length > sizeof(ushort))
                {
                    PackPair(ref nibbles, ref bytes, 1);
                }
            }
            else if (length != 0)
            {
                bytes = (byte)((nibbles << 4) | Unsafe.Add(ref nibbles, 1));
            }
        }

        /// <summary>Length of the common prefix of two nibble keys.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int CommonPrefixLength(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
            => left.CommonPrefixLength(right);

        // Every width below ends with one block aligned to the end of the input. It may overlap the block before
        // it and rewrites the same values there, so no scalar tail remains.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ExpandVectors(ref byte bytes, ref byte nibbles, nuint length)
        {
            nuint last;
            if (Avx2.IsSupported && length >= (nuint)Vector256<byte>.Count)
            {
                last = length - (nuint)Vector256<byte>.Count;
                for (nuint i = 0; i < last; i += (nuint)Vector256<byte>.Count)
                {
                    Expand256(ref bytes, ref nibbles, i);
                }

                Expand256(ref bytes, ref nibbles, last);
            }
            else if (length >= (nuint)Vector128<byte>.Count)
            {
                last = length - (nuint)Vector128<byte>.Count;
                for (nuint i = 0; i < last; i += (nuint)Vector128<byte>.Count)
                {
                    Expand128(ref bytes, ref nibbles, i);
                }

                Expand128(ref bytes, ref nibbles, last);
            }
            else if (length >= sizeof(ulong))
            {
                last = length - sizeof(ulong);
                // Arm64 loads straight into vector lanes; x64 does not accelerate Vector64.
                Vector128<byte> value = AdvSimd.Arm64.IsSupported
                    ? Vector128.Create(Vector64.LoadUnsafe(ref bytes), Vector64.LoadUnsafe(ref bytes, last))
                    : Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref bytes), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, last))).AsByte();
                Vector128<byte> high = Vector128.ShiftRightLogical(value, 4);
                Vector128<byte> low = value & Vector128.Create((byte)0x0F);
                InterleaveLower(high, low).StoreUnsafe(ref nibbles);
                InterleaveUpper(high, low).StoreUnsafe(ref nibbles, last * 2);
            }
            else
            {
                last = length - sizeof(uint);
                if (AdvSimd.Arm64.IsSupported)
                {
                    // Each half gets its own 64-bit lane, so both results are low halves and store without a lane move.
                    // Float loads go straight to vector registers; a load or move never changes the bits.
                    Vector128<byte> value = Vector128.Create(
                        Vector64.CreateScalarUnsafe(Unsafe.ReadUnaligned<float>(ref bytes)),
                        Vector64.CreateScalarUnsafe(Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref bytes, last)))).AsByte();
                    Vector128<byte> high = Vector128.ShiftRightLogical(value, 4);
                    Vector128<byte> low = value & Vector128.Create((byte)0x0F);
                    AdvSimd.Arm64.ZipLow(high, low).GetLower().StoreUnsafe(ref nibbles);
                    AdvSimd.Arm64.ZipHigh(high, low).GetLower().StoreUnsafe(ref nibbles, last * 2);
                }
                else
                {
                    Vector128<byte> value = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref bytes))
                        .WithElement(1, Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref bytes, last))).AsByte();
                    Vector128<ulong> expanded = InterleaveLower(Vector128.ShiftRightLogical(value, 4), value & Vector128.Create((byte)0x0F)).AsUInt64();
                    Unsafe.WriteUnaligned(ref nibbles, expanded.ToScalar());
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref nibbles, last * 2), expanded.GetElement(1));
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void PackVectors(ref byte nibbles, ref byte bytes, nuint length)
        {
            nuint last;
            if (Avx2.IsSupported && length >= (nuint)Vector256<byte>.Count)
            {
                last = length - (nuint)Vector256<byte>.Count;
                for (nuint i = 0; i < last; i += (nuint)Vector256<byte>.Count)
                {
                    Pack256(ref nibbles, ref bytes, i);
                }

                Pack256(ref nibbles, ref bytes, last);
            }
            else if (length >= (nuint)Vector128<byte>.Count)
            {
                last = length - (nuint)Vector128<byte>.Count;
                for (nuint i = 0; i < last; i += (nuint)Vector128<byte>.Count)
                {
                    ref byte source = ref Unsafe.Add(ref nibbles, i * 2);
                    PackPairs(Vector128.LoadUnsafe(ref source), Vector128.LoadUnsafe(ref source, (nuint)Vector128<byte>.Count)).StoreUnsafe(ref bytes, i);
                }

                ref byte lastSource = ref Unsafe.Add(ref nibbles, last * 2);
                PackPairs(Vector128.LoadUnsafe(ref lastSource), Vector128.LoadUnsafe(ref lastSource, (nuint)Vector128<byte>.Count)).StoreUnsafe(ref bytes, last);
            }
            else if (length >= sizeof(ulong))
            {
                last = length - sizeof(ulong);
                Vector128<ulong> packed = PackPairs(Vector128.LoadUnsafe(ref nibbles), Vector128.LoadUnsafe(ref nibbles, last * 2)).AsUInt64();
                Unsafe.WriteUnaligned(ref bytes, packed.ToScalar());
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref bytes, last), packed.GetElement(1));
            }
            else
            {
                last = length - sizeof(uint);
                // Each half packs from its own register into its own 64-bit lane, so no lane insert is needed.
                // Double loads go straight to vector registers; a load or move never changes the bits.
                Vector128<uint> packed = PackPairs(
                    Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<double>(ref nibbles)).AsByte(),
                    Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<double>(ref Unsafe.Add(ref nibbles, last * 2))).AsByte()).AsUInt32();
                Unsafe.WriteUnaligned(ref bytes, packed.ToScalar());
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref bytes, last), packed.GetElement(2));
            }
        }

        private static void ExpandWords(ref byte bytes, ref byte nibbles, nuint length)
        {
            if (length >= sizeof(ulong))
            {
                nuint last = length - sizeof(ulong);
                for (nuint i = 0; i < last; i += sizeof(ulong))
                {
                    ExpandLong(ref bytes, ref nibbles, i);
                }

                ExpandLong(ref bytes, ref nibbles, last);
            }
            else
            {
                nuint last = length - sizeof(uint);
                Unsafe.WriteUnaligned(ref nibbles, Spread(Unsafe.ReadUnaligned<uint>(ref bytes)));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref nibbles, last * 2), Spread(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref bytes, last))));
            }
        }

        private static void PackWords(ref byte nibbles, ref byte bytes, nuint length)
        {
            if (length >= sizeof(ulong))
            {
                nuint last = length - sizeof(ulong);
                for (nuint i = 0; i < last; i += sizeof(ulong))
                {
                    PackLong(ref nibbles, ref bytes, i);
                }

                PackLong(ref nibbles, ref bytes, last);
            }
            else
            {
                nuint last = length - sizeof(uint);
                Unsafe.WriteUnaligned(ref bytes, Fold(Unsafe.ReadUnaligned<ulong>(ref nibbles)));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref bytes, last), Fold(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref nibbles, last * 2))));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Expand256(ref byte bytes, ref byte nibbles, nuint index)
        {
            // Unpack works per 128-bit lane; quadword order 0, 2, 1, 3 makes the results contiguous.
            Vector256<byte> value = Avx2.Permute4x64(Vector256.LoadUnsafe(ref bytes, index).AsUInt64(), 0b11_01_10_00).AsByte();
            Vector256<byte> high = Vector256.ShiftRightLogical(value, 4);
            Vector256<byte> low = value & Vector256.Create((byte)0x0F);
            ref byte destination = ref Unsafe.Add(ref nibbles, index * 2);
            Avx2.UnpackLow(high, low).StoreUnsafe(ref destination);
            Avx2.UnpackHigh(high, low).StoreUnsafe(ref destination, (nuint)Vector256<byte>.Count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Expand128(ref byte bytes, ref byte nibbles, nuint index)
        {
            Vector128<byte> value = Vector128.LoadUnsafe(ref bytes, index);
            Vector128<byte> high = Vector128.ShiftRightLogical(value, 4);
            Vector128<byte> low = value & Vector128.Create((byte)0x0F);
            ref byte destination = ref Unsafe.Add(ref nibbles, index * 2);
            InterleaveLower(high, low).StoreUnsafe(ref destination);
            InterleaveUpper(high, low).StoreUnsafe(ref destination, (nuint)Vector128<byte>.Count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> InterleaveLower(Vector128<byte> high, Vector128<byte> low) =>
            Sse2.IsSupported ? Sse2.UnpackLow(high, low) : AdvSimd.Arm64.ZipLow(high, low);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> InterleaveUpper(Vector128<byte> high, Vector128<byte> low) =>
            Sse2.IsSupported ? Sse2.UnpackHigh(high, low) : AdvSimd.Arm64.ZipHigh(high, low);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Pack256(ref byte nibbles, ref byte bytes, nuint index)
        {
            ref byte source = ref Unsafe.Add(ref nibbles, index * 2);
            Vector256<sbyte> weights = Vector256.Create((ushort)0x0110).AsSByte();
            Vector256<byte> packed = Avx2.PackUnsignedSaturate(
                Avx2.MultiplyAddAdjacent(Vector256.LoadUnsafe(ref source), weights),
                Avx2.MultiplyAddAdjacent(Vector256.LoadUnsafe(ref source, (nuint)Vector256<byte>.Count), weights));
            // Pack works per 128-bit lane; quadword order 0, 2, 1, 3 makes the result contiguous.
            Avx2.Permute4x64(packed.AsUInt64(), 0b11_01_10_00).AsByte().StoreUnsafe(ref bytes, index);
        }

        /// <summary>Packs 32 nibble bytes, 16 from each argument, into 16 bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> PackPairs(Vector128<byte> first, Vector128<byte> second)
        {
            if (Ssse3.IsSupported)
            {
                Vector128<sbyte> weights = Vector128.Create((ushort)0x0110).AsSByte();
                return Sse2.PackUnsignedSaturate(Ssse3.MultiplyAddAdjacent(first, weights), Ssse3.MultiplyAddAdjacent(second, weights));
            }

            return AdvSimd.ShiftLeftAndInsert(AdvSimd.Arm64.UnzipOdd(first, second), AdvSimd.Arm64.UnzipEven(first, second), 4);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ExpandLong(ref byte bytes, ref byte nibbles, nuint index)
        {
            ulong value = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, index));
            ref byte destination = ref Unsafe.Add(ref nibbles, index * 2);
            Unsafe.WriteUnaligned(ref destination, Spread((uint)value));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, sizeof(ulong)), Spread((uint)(value >> 32)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void PackLong(ref byte nibbles, ref byte bytes, nuint index)
        {
            ref byte source = ref Unsafe.Add(ref nibbles, index * 2);
            uint lower = Fold(Unsafe.ReadUnaligned<ulong>(ref source));
            uint upper = Fold(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, sizeof(ulong))));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref bytes, index), lower | ((ulong)upper << 32));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ExpandPair(ref byte bytes, ref byte nibbles, nuint index)
        {
            uint v = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref bytes, index));
            v = (v | (v << 8)) & 0x00FF00FFU;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref nibbles, index * 2), ((v >> 4) & 0x000F000FU) | ((v & 0x000F000FU) << 8));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void PackPair(ref byte nibbles, ref byte bytes, nuint index)
        {
            uint pairs = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref nibbles, index * 2));
            uint packed = ((pairs << 4) | (pairs >> 8)) & 0x00FF00FFU;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref bytes, index), (ushort)(packed | (packed >> 8)));
        }

        /// <summary>Spreads four bytes into eight nibble bytes, high nibble first.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Spread(uint value)
        {
            ulong v = value;
            v = (v | (v << 16)) & 0x0000FFFF0000FFFFUL;
            v = (v | (v << 8)) & 0x00FF00FF00FF00FFUL;
            return ((v >> 4) & 0x000F000F000F000FUL) | ((v & 0x000F000F000F000FUL) << 8);
        }

        /// <summary>Folds eight nibble bytes, high nibble first, into four bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Fold(ulong pairs)
        {
            ulong packed = ((pairs << 4) | (pairs >> 8)) & 0x00FF00FF00FF00FFUL;
            packed = (packed | (packed >> 8)) & 0x0000FFFF0000FFFFUL;
            return (uint)(packed | (packed >> 16));
        }
    }
}
