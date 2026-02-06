// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

using Nethermind.Core.Collections;

[assembly: InternalsVisibleTo("Nethermind.Benchmark")]
namespace Nethermind.Core.Extensions
{
    // copied and bit modified from: https://github.com/dotnet/runtime/blob/main/src/libraries/Common/src/System/HexConverter.cs
    // TODO: Revisit when upgrading .NET for additional optimizations
    public static class HexConverter
    {
        public enum Casing : uint
        {
            // Output [ '0' .. '9' ] and [ 'A' .. 'F' ].
            Upper = 0,

            // Output [ '0' .. '9' ] and [ 'a' .. 'f' ].
            // This works because values in the range [ 0x30 .. 0x39 ] ([ '0' .. '9' ])
            // already have the 0x20 bit set, so ORing them with 0x20 is a no-op,
            // while outputs in the range [ 0x41 .. 0x46 ] ([ 'A' .. 'F' ])
            // don't have the 0x20 bit set, so ORing them maps to
            // [ 0x61 .. 0x66 ] ([ 'a' .. 'f' ]), which is what we want.
            Lower = 0x2020U,
        }

        // We want to pack the incoming byte into a single integer [ 0000 HHHH 0000 LLLL ],
        // where HHHH and LLLL are the high and low nibbles of the incoming byte. Then
        // subtract this integer from a constant minuend as shown below.
        //
        //   [ 1000 1001 1000 1001 ]
        // - [ 0000 HHHH 0000 LLLL ]
        // =========================
        //   [ *YYY **** *ZZZ **** ]
        //
        // The end result of this is that YYY is 0b000 if HHHH <= 9, and YYY is 0b111 if HHHH >= 10.
        // Similarly, ZZZ is 0b000 if LLLL <= 9, and ZZZ is 0b111 if LLLL >= 10.
        // (We don't care about the value of asterisked bits.)
        //
        // To turn a nibble in the range [ 0 .. 9 ] into hex, we calculate hex := nibble + 48 (ascii '0').
        // To turn a nibble in the range [ 10 .. 15 ] into hex, we calculate hex := nibble - 10 + 65 (ascii 'A').
        //                                                                => hex := nibble + 55.
        // The difference in the starting ASCII offset is (55 - 48) = 7, depending on whether the nibble is <= 9 or >= 10.
        // Since 7 is 0b111, this conveniently matches the YYY or ZZZ value computed during the earlier subtraction.

        // The commented out code below is code that directly implements the logic described above.

        // uint packedOriginalValues = (((uint)value & 0xF0U) << 4) + ((uint)value & 0x0FU);
        // uint difference = 0x8989U - packedOriginalValues;
        // uint add7Mask = (difference & 0x7070U) >> 4; // line YYY and ZZZ back up with the packed values
        // uint packedResult = packedOriginalValues + add7Mask + 0x3030U /* ascii '0' */;

        // The code below is equivalent to the commented out code above but has been tweaked
        // to allow codegen to make some extra optimizations.

        // The low byte of the packed result contains the hex representation of the incoming byte's low nibble.
        // The adjacent byte of the packed result contains the hex representation of the incoming byte's high nibble.

        // Finally, write to the output buffer starting with the *highest* index so that codegen can
        // elide all but the first bounds check. (This only works if 'startingIndex' is a compile-time constant.)

        // The JIT can elide bounds checks if 'startingIndex' is constant and if the caller is
        // writing to a span of known length (or the caller has already checked the bounds of the
        // furthest access).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ToBytesBuffer(byte value, Span<byte> buffer, int startingIndex = 0, Casing casing = Casing.Lower)
        {
            uint difference = ((value & 0xF0U) << 4) + (value & 0x0FU) - 0x8989U;
            uint packedResult = ((((uint)(-(int)difference) & 0x7070U) >> 4) + difference + 0xB9B9U) | (uint)casing;

            buffer[startingIndex + 1] = (byte)packedResult;
            buffer[startingIndex] = (byte)(packedResult >> 8);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ToCharsBuffer(byte value, Span<char> buffer, int startingIndex = 0, Casing casing = Casing.Lower)
        {
            uint difference = (((uint)value & 0xF0U) << 4) + ((uint)value & 0x0FU) - 0x8989U;
            uint packedResult = ((((uint)(-(int)difference) & 0x7070U) >> 4) + difference + 0xB9B9U) | (uint)casing;

            buffer[startingIndex + 1] = (char)(packedResult & 0xFF);
            buffer[startingIndex] = (char)(packedResult >> 8);
        }

        private static void EncodeToUtf16_Vector128(ReadOnlySpan<byte> bytes, Span<char> chars, Casing casing)
        {
            Vector128<byte> shuffleMask = Vector128.Create(
                0xFF, 0xFF, 0, 0xFF, 0xFF, 0xFF, 1, 0xFF,
                0xFF, 0xFF, 2, 0xFF, 0xFF, 0xFF, 3, 0xFF);

            Vector128<byte> asciiTable = (casing == Casing.Upper) ?
                Vector128.Create((byte)'0', (byte)'1', (byte)'2', (byte)'3',
                                 (byte)'4', (byte)'5', (byte)'6', (byte)'7',
                                 (byte)'8', (byte)'9', (byte)'A', (byte)'B',
                                 (byte)'C', (byte)'D', (byte)'E', (byte)'F') :
                Vector128.Create((byte)'0', (byte)'1', (byte)'2', (byte)'3',
                                 (byte)'4', (byte)'5', (byte)'6', (byte)'7',
                                 (byte)'8', (byte)'9', (byte)'a', (byte)'b',
                                 (byte)'c', (byte)'d', (byte)'e', (byte)'f');

            nuint pos = 0;
            Debug.Assert(bytes.Length >= 4);

            // it's used to ensure we can process the trailing elements in the same SIMD loop (with possible overlap)
            // but we won't double compute for any evenly divisible by 4 length since we
            // compare pos > lengthSubVector128 rather than pos >= lengthSubVector128
            nuint lengthSubVector128 = (nuint)bytes.Length - (nuint)Vector128<int>.Count;
            ref byte destRef = ref Unsafe.As<char, byte>(ref MemoryMarshal.GetReference(chars));
            do
            {
                // Read 32bits from "bytes" span at "pos" offset
                uint block = Unsafe.ReadUnaligned<uint>(
                    ref Unsafe.Add(ref MemoryMarshal.GetReference(bytes), pos));

                // TODO: Remove once cross-platform Shuffle is landed
                // https://github.com/dotnet/runtime/issues/63331
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                static Vector128<byte> Shuffle(Vector128<byte> value, Vector128<byte> mask)
                {
                    if (Ssse3.IsSupported)
                    {
                        return Ssse3.Shuffle(value, mask);
                    }

                    if (AdvSimd.Arm64.IsSupported)
                    {
                        return AdvSimd.Arm64.VectorTableLookup(value, mask);
                    }

                    throw new NotSupportedException();
                }

                // Calculate nibbles
                Vector128<byte> lowNibbles = Shuffle(
                    Vector128.CreateScalarUnsafe(block).AsByte(), shuffleMask);

                // ExtractVector128 is not entirely the same as ShiftRightLogical128BitLane, but it works here since
                // first two bytes in lowNibbles are guaranteed to be zeros
                Vector128<byte> shifted = Sse2.IsSupported ?
                    Sse2.ShiftRightLogical128BitLane(lowNibbles, 2) :
                    AdvSimd.ExtractVector128(lowNibbles, lowNibbles, 2);

                Vector128<byte> highNibbles = Vector128.ShiftRightLogical(shifted.AsInt32(), 4).AsByte();

                // Lookup the hex values at the positions of the indices
                Vector128<byte> indices = (lowNibbles | highNibbles) & Vector128.Create((byte)0xF);
                Vector128<byte> hex = Shuffle(asciiTable, indices);

                // The high bytes (0x00) of the chars have also been converted
                // to ascii hex '0', so clear them out.
                hex &= Vector128.Create((ushort)0xFF).AsByte();
                hex.StoreUnsafe(ref destRef, pos * 4); // we encode 4 bytes as a single char (0x0-0xF)
                pos += (nuint)Vector128<int>.Count;

                if (pos == (nuint)bytes.Length)
                {
                    return;
                }

                // Overlap with the current chunk for trailing elements
                if (pos > lengthSubVector128)
                {
                    pos = lengthSubVector128;
                }

            } while (true);
        }

        public static void EncodeToUtf16(ReadOnlySpan<byte> bytes, Span<char> chars, Casing casing = Casing.Lower)
        {
            Debug.Assert(chars.Length >= bytes.Length * 2);

            if ((AdvSimd.Arm64.IsSupported || Ssse3.IsSupported) && bytes.Length >= 4)
            {
                EncodeToUtf16_Vector128(bytes, chars, casing);
                return;
            }
            for (int pos = 0; pos < bytes.Length; pos++)
            {
                ToCharsBuffer(bytes[pos], chars, pos * 2, casing);
            }
        }

        public static unsafe string ToString(ReadOnlySpan<byte> bytes, Casing casing = Casing.Lower)
        {
            fixed (byte* bytesPtr = bytes)
            {
                return string.Create(bytes.Length * 2, (Ptr: (IntPtr)bytesPtr, bytes.Length, casing), static (chars, args) =>
                {
                    var ros = new ReadOnlySpan<byte>((byte*)args.Ptr, args.Length);
                    EncodeToUtf16(ros, chars, args.casing);
                });
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static char ToCharUpper(int value)
        {
            value &= 0xF;
            value += '0';

            if (value > '9')
            {
                value += ('A' - ('9' + 1));
            }

            return (char)value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static char ToCharLower(int value)
        {
            value &= 0xF;
            value += '0';

            if (value > '9')
            {
                value += ('a' - ('9' + 1));
            }

            return (char)value;
        }

        public static bool TryDecodeFromUtf8(ReadOnlySpan<byte> hexString, Span<byte> result)
        {
            int oddMod = hexString.Length % 2;
            if (oddMod == 0 && BitConverter.IsLittleEndian && (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported) &&
                hexString.Length >= Vector128<byte>.Count)
            {
                if (Avx512BW.IsSupported && hexString.Length >= Vector512<byte>.Count)
                {
                    return TryDecodeFromUtf8_Vector512(hexString, result);
                }
                else if (Avx2.IsSupported && hexString.Length >= Vector256<byte>.Count)
                {
                    return TryDecodeFromUtf8_Vector256(hexString, result);
                }
                else
                {
                    return TryDecodeFromUtf8_Vector128(hexString, result);
                }
            }
            else
            {
                return TryDecodeFromUtf8_Scalar(hexString, result, oddMod == 1);
            }
        }

        /// <summary>
        /// Loops in 2 byte chunks and decodes them into 1 byte chunks.
        /// Unrolled 2x to allow parallel lookups without register spilling.
        /// </summary>
        [SkipLocalsInit]
        internal static bool TryDecodeFromUtf8_Scalar(ReadOnlySpan<byte> hex, Span<byte> bytes, bool isOdd)
        {
            Debug.Assert((hex.Length / 2) + (hex.Length % 2) == bytes.Length, "Target buffer not right-sized for provided characters");

            ref byte hexRef = ref MemoryMarshal.GetReference(hex);
            ref byte bytesRef = ref MemoryMarshal.GetReference(bytes);
            ref byte lookupRef = ref MemoryMarshal.GetReference(CharToHexLookup);
            nuint i = 0;
            nuint j = 0;
            nuint bytesLength = (nuint)bytes.Length;

            if (isOdd)
            {
                int n0 = Unsafe.Add(ref lookupRef, Unsafe.Add(ref hexRef, i++));
                if (n0 == 0xFF)
                    return false;
                Unsafe.Add(ref bytesRef, j++) = (byte)n0;
            }

            // Unrolled 2x: process 4 hex chars (2 output bytes) per iteration
            // 4 independent lookups fit in available registers without spilling
            while (j + 2 <= bytesLength)
            {
                // 4 independent lookups - can execute in parallel on OoO CPUs
                int n0 = Unsafe.Add(ref lookupRef, Unsafe.Add(ref hexRef, i));
                int n1 = Unsafe.Add(ref lookupRef, Unsafe.Add(ref hexRef, i + 1));
                int n2 = Unsafe.Add(ref lookupRef, Unsafe.Add(ref hexRef, i + 2));
                int n3 = Unsafe.Add(ref lookupRef, Unsafe.Add(ref hexRef, i + 3));

                // Single validity check for all 4 nibbles
                if (((n0 | n1 | n2 | n3) & 0xF0) != 0)
                    return false;

                // Combine nibbles into bytes and store
                Unsafe.Add(ref bytesRef, j) = (byte)((n0 << 4) | n1);
                Unsafe.Add(ref bytesRef, j + 1) = (byte)((n2 << 4) | n3);

                i += 4;
                j += 2;
            }

            // Handle remaining 0-1 bytes
            if (j < bytesLength)
            {
                int n0 = Unsafe.Add(ref lookupRef, Unsafe.Add(ref hexRef, i));
                int n1 = Unsafe.Add(ref lookupRef, Unsafe.Add(ref hexRef, i + 1));

                if (((n0 | n1) & 0xF0) != 0)
                    return false;

                Unsafe.Add(ref bytesRef, j) = (byte)((n0 << 4) | n1);
            }

            return true;
        }

        /// <summary>
        /// Loops in 16 byte chunks and decodes them into 8 byte chunks.
        /// Unrolled 2x to process 32 hex bytes per iteration when possible.
        /// </summary>
        [SkipLocalsInit]
        internal static bool TryDecodeFromUtf8_Vector128(ReadOnlySpan<byte> hex, Span<byte> bytes)
        {
            Debug.Assert(Ssse3.IsSupported || AdvSimd.Arm64.IsSupported);
            Debug.Assert((hex.Length / 2) + (hex.Length % 2) == bytes.Length);
            Debug.Assert(hex.Length >= Vector128<byte>.Count);

            nuint offset = 0;
            nuint hexLength = (nuint)hex.Length;

            ref byte srcRef = ref MemoryMarshal.GetReference(hex);
            ref byte destRef = ref MemoryMarshal.GetReference(bytes);

            Vector128<byte> shuf = Vector128.Create((byte)0, 2, 4, 6, 8, 10, 12, 14, 0, 0, 0, 0, 0, 0, 0, 0);

            // 2x unrolled loop: process 32 hex bytes -> 16 output bytes per iteration
            while (offset + (nuint)Vector128<byte>.Count * 2 <= hexLength)
            {
                Vector128<byte> vec0 = Vector128.LoadUnsafe(ref srcRef, offset);
                Vector128<byte> vec1 = Vector128.LoadUnsafe(ref srcRef, offset + (nuint)Vector128<byte>.Count);

                // Process vec0
                Vector128<byte> t1_0 = vec0 + Vector128.Create((byte)(0xFF - '9'));
                Vector128<byte> t2_0 = SubtractSaturate(t1_0, Vector128.Create((byte)6));
                Vector128<byte> t3_0 = (vec0 & Vector128.Create((byte)0xDF)) - Vector128.Create((byte)'A');
                Vector128<byte> t4_0 = AddSaturate(t3_0, Vector128.Create((byte)10));
                Vector128<byte> nibbles0 = Vector128.Min(t2_0 - Vector128.Create((byte)0xF0), t4_0);

                // Process vec1
                Vector128<byte> t1_1 = vec1 + Vector128.Create((byte)(0xFF - '9'));
                Vector128<byte> t2_1 = SubtractSaturate(t1_1, Vector128.Create((byte)6));
                Vector128<byte> t3_1 = (vec1 & Vector128.Create((byte)0xDF)) - Vector128.Create((byte)'A');
                Vector128<byte> t4_1 = AddSaturate(t3_1, Vector128.Create((byte)10));
                Vector128<byte> nibbles1 = Vector128.Min(t2_1 - Vector128.Create((byte)0xF0), t4_1);

                // Combined validity check
                Vector128<byte> invalid0 = (vec0 & Vector128.Create((byte)0x80)) | AddSaturate(nibbles0, Vector128.Create((byte)(127 - 15)));
                Vector128<byte> invalid1 = (vec1 & Vector128.Create((byte)0x80)) | AddSaturate(nibbles1, Vector128.Create((byte)(127 - 15)));
                if ((invalid0 | invalid1).ExtractMostSignificantBits() != 0)
                    return false;

                // Convert nibbles to bytes
                Vector128<byte> output0, output1;
                if (Ssse3.IsSupported)
                {
                    output0 = Ssse3.MultiplyAddAdjacent(nibbles0, Vector128.Create((short)0x0110).AsSByte()).AsByte();
                    output1 = Ssse3.MultiplyAddAdjacent(nibbles1, Vector128.Create((short)0x0110).AsSByte()).AsByte();
                }
                else
                {
                    Vector128<short> even0 = AdvSimd.Arm64.TransposeEven(nibbles0, Vector128<byte>.Zero).AsInt16();
                    Vector128<short> odd0 = AdvSimd.Arm64.TransposeOdd(nibbles0, Vector128<byte>.Zero).AsInt16();
                    even0 = AdvSimd.ShiftLeftLogical(even0, 4).AsInt16();
                    output0 = AdvSimd.AddSaturate(even0, odd0).AsByte();

                    Vector128<short> even1 = AdvSimd.Arm64.TransposeEven(nibbles1, Vector128<byte>.Zero).AsInt16();
                    Vector128<short> odd1 = AdvSimd.Arm64.TransposeOdd(nibbles1, Vector128<byte>.Zero).AsInt16();
                    even1 = AdvSimd.ShiftLeftLogical(even1, 4).AsInt16();
                    output1 = AdvSimd.AddSaturate(even1, odd1).AsByte();
                }

                output0 = Vector128.Shuffle(output0, shuf);
                output1 = Vector128.Shuffle(output1, shuf);

                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destRef, offset / 2), output0.AsUInt64().ToScalar());
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destRef, offset / 2 + 8), output1.AsUInt64().ToScalar());

                offset += (nuint)Vector128<byte>.Count * 2;
            }

            // Remainder: 1x loop for remaining 16-31 bytes
            while (offset + (nuint)Vector128<byte>.Count <= hexLength)
            {
                Vector128<byte> vec = Vector128.LoadUnsafe(ref srcRef, offset);

                Vector128<byte> t1 = vec + Vector128.Create((byte)(0xFF - '9'));
                Vector128<byte> t2 = SubtractSaturate(t1, Vector128.Create((byte)6));
                Vector128<byte> t3 = (vec & Vector128.Create((byte)0xDF)) - Vector128.Create((byte)'A');
                Vector128<byte> t4 = AddSaturate(t3, Vector128.Create((byte)10));
                Vector128<byte> nibbles = Vector128.Min(t2 - Vector128.Create((byte)0xF0), t4);

                Vector128<byte> invalid = (vec & Vector128.Create((byte)0x80)) | AddSaturate(nibbles, Vector128.Create((byte)(127 - 15)));
                if (invalid.ExtractMostSignificantBits() != 0)
                    return false;

                Vector128<byte> output;
                if (Ssse3.IsSupported)
                {
                    output = Ssse3.MultiplyAddAdjacent(nibbles, Vector128.Create((short)0x0110).AsSByte()).AsByte();
                }
                else
                {
                    Vector128<short> even = AdvSimd.Arm64.TransposeEven(nibbles, Vector128<byte>.Zero).AsInt16();
                    Vector128<short> odd = AdvSimd.Arm64.TransposeOdd(nibbles, Vector128<byte>.Zero).AsInt16();
                    even = AdvSimd.ShiftLeftLogical(even, 4).AsInt16();
                    output = AdvSimd.AddSaturate(even, odd).AsByte();
                }

                output = Vector128.Shuffle(output, shuf);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destRef, offset / 2), output.AsUInt64().ToScalar());

                offset += (nuint)Vector128<byte>.Count;
            }

            // Handle trailing < 16 bytes with overlap
            if (offset < hexLength)
            {
                offset = hexLength - (nuint)Vector128<byte>.Count;
                Vector128<byte> vec = Vector128.LoadUnsafe(ref srcRef, offset);

                Vector128<byte> t1 = vec + Vector128.Create((byte)(0xFF - '9'));
                Vector128<byte> t2 = SubtractSaturate(t1, Vector128.Create((byte)6));
                Vector128<byte> t3 = (vec & Vector128.Create((byte)0xDF)) - Vector128.Create((byte)'A');
                Vector128<byte> t4 = AddSaturate(t3, Vector128.Create((byte)10));
                Vector128<byte> nibbles = Vector128.Min(t2 - Vector128.Create((byte)0xF0), t4);

                Vector128<byte> invalid = (vec & Vector128.Create((byte)0x80)) | AddSaturate(nibbles, Vector128.Create((byte)(127 - 15)));
                if (invalid.ExtractMostSignificantBits() != 0)
                    return false;

                Vector128<byte> output;
                if (Ssse3.IsSupported)
                {
                    output = Ssse3.MultiplyAddAdjacent(nibbles, Vector128.Create((short)0x0110).AsSByte()).AsByte();
                }
                else
                {
                    Vector128<short> even = AdvSimd.Arm64.TransposeEven(nibbles, Vector128<byte>.Zero).AsInt16();
                    Vector128<short> odd = AdvSimd.Arm64.TransposeOdd(nibbles, Vector128<byte>.Zero).AsInt16();
                    even = AdvSimd.ShiftLeftLogical(even, 4).AsInt16();
                    output = AdvSimd.AddSaturate(even, odd).AsByte();
                }

                output = Vector128.Shuffle(output, shuf);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destRef, offset / 2), output.AsUInt64().ToScalar());
            }

            return true;
        }

        /// <summary>
        /// Loops in 32 byte chunks and decodes them into 16 byte chunks.
        /// Unrolled 2x to process 64 hex bytes per iteration when possible.
        /// </summary>
        [SkipLocalsInit]
        internal static bool TryDecodeFromUtf8_Vector256(ReadOnlySpan<byte> hex, Span<byte> bytes)
        {
            Debug.Assert(Avx2.IsSupported);
            Debug.Assert((hex.Length / 2) + (hex.Length % 2) == bytes.Length);
            Debug.Assert(hex.Length >= Vector256<byte>.Count);

            nuint offset = 0;
            nuint hexLength = (nuint)hex.Length;

            ref byte srcRef = ref MemoryMarshal.GetReference(hex);
            ref byte destRef = ref MemoryMarshal.GetReference(bytes);

            // 2x unrolled loop: process 64 hex bytes -> 32 output bytes per iteration
            // Use addition instead of subtraction to avoid unsigned underflow
            while (offset + (nuint)Vector256<byte>.Count * 2 <= hexLength)
            {
                Vector256<byte> vec0 = Vector256.LoadUnsafe(ref srcRef, offset);
                Vector256<byte> vec1 = Vector256.LoadUnsafe(ref srcRef, offset + (nuint)Vector256<byte>.Count);

                // Process vec0: hex decode algorithm
                Vector256<byte> t1_0 = vec0 + Vector256.Create((byte)(0xFF - '9'));
                Vector256<byte> t2_0 = Avx2.SubtractSaturate(t1_0, Vector256.Create((byte)6));
                Vector256<byte> t3_0 = (vec0 & Vector256.Create((byte)0xDF)) - Vector256.Create((byte)'A');
                Vector256<byte> t4_0 = Avx2.AddSaturate(t3_0, Vector256.Create((byte)10));
                Vector256<byte> nibbles0 = Vector256.Min(t2_0 - Vector256.Create((byte)0xF0), t4_0);

                // Process vec1: hex decode algorithm
                Vector256<byte> t1_1 = vec1 + Vector256.Create((byte)(0xFF - '9'));
                Vector256<byte> t2_1 = Avx2.SubtractSaturate(t1_1, Vector256.Create((byte)6));
                Vector256<byte> t3_1 = (vec1 & Vector256.Create((byte)0xDF)) - Vector256.Create((byte)'A');
                Vector256<byte> t4_1 = Avx2.AddSaturate(t3_1, Vector256.Create((byte)10));
                Vector256<byte> nibbles1 = Vector256.Min(t2_1 - Vector256.Create((byte)0xF0), t4_1);

                // Combined validity check for both vectors
                Vector256<byte> invalid0 = (vec0 & Vector256.Create((byte)0x80)) | Avx2.AddSaturate(nibbles0, Vector256.Create((byte)(127 - 15)));
                Vector256<byte> invalid1 = (vec1 & Vector256.Create((byte)0x80)) | Avx2.AddSaturate(nibbles1, Vector256.Create((byte)(127 - 15)));
                if ((invalid0 | invalid1).ExtractMostSignificantBits() != 0)
                    return false;

                // Convert nibbles to bytes and shuffle
                Vector256<byte> output0 = Avx2.MultiplyAddAdjacent(nibbles0, Vector256.Create((short)0x0110).AsSByte()).AsByte();
                Vector256<byte> output1 = Avx2.MultiplyAddAdjacent(nibbles1, Vector256.Create((short)0x0110).AsSByte()).AsByte();
                Vector256<byte> shuf = Vector256.Create((byte)0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
                output0 = Vector256.Shuffle(output0, shuf);
                output1 = Vector256.Shuffle(output1, shuf);

                // Store 32 bytes total
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destRef, offset / 2), output0.AsUInt64().GetLower());
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destRef, offset / 2 + 16), output1.AsUInt64().GetLower());

                offset += (nuint)Vector256<byte>.Count * 2;
            }

            // Remainder: 1x loop for remaining 32-63 bytes
            while (offset + (nuint)Vector256<byte>.Count <= hexLength)
            {
                Vector256<byte> vec = Vector256.LoadUnsafe(ref srcRef, offset);

                Vector256<byte> t1 = vec + Vector256.Create((byte)(0xFF - '9'));
                Vector256<byte> t2 = Avx2.SubtractSaturate(t1, Vector256.Create((byte)6));
                Vector256<byte> t3 = (vec & Vector256.Create((byte)0xDF)) - Vector256.Create((byte)'A');
                Vector256<byte> t4 = Avx2.AddSaturate(t3, Vector256.Create((byte)10));
                Vector256<byte> nibbles = Vector256.Min(t2 - Vector256.Create((byte)0xF0), t4);

                Vector256<byte> invalid = (vec & Vector256.Create((byte)0x80)) | Avx2.AddSaturate(nibbles, Vector256.Create((byte)(127 - 15)));
                if (invalid.ExtractMostSignificantBits() != 0)
                    return false;

                Vector256<byte> output = Avx2.MultiplyAddAdjacent(nibbles, Vector256.Create((short)0x0110).AsSByte()).AsByte();
                output = Vector256.Shuffle(output, Vector256.Create((byte)0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destRef, offset / 2), output.AsUInt64().GetLower());

                offset += (nuint)Vector256<byte>.Count;
            }

            // Handle trailing < 32 bytes with overlap
            if (offset < hexLength)
            {
                offset = hexLength - (nuint)Vector256<byte>.Count;
                Vector256<byte> vec = Vector256.LoadUnsafe(ref srcRef, offset);

                Vector256<byte> t1 = vec + Vector256.Create((byte)(0xFF - '9'));
                Vector256<byte> t2 = Avx2.SubtractSaturate(t1, Vector256.Create((byte)6));
                Vector256<byte> t3 = (vec & Vector256.Create((byte)0xDF)) - Vector256.Create((byte)'A');
                Vector256<byte> t4 = Avx2.AddSaturate(t3, Vector256.Create((byte)10));
                Vector256<byte> nibbles = Vector256.Min(t2 - Vector256.Create((byte)0xF0), t4);

                Vector256<byte> invalid = (vec & Vector256.Create((byte)0x80)) | Avx2.AddSaturate(nibbles, Vector256.Create((byte)(127 - 15)));
                if (invalid.ExtractMostSignificantBits() != 0)
                    return false;

                Vector256<byte> output = Avx2.MultiplyAddAdjacent(nibbles, Vector256.Create((short)0x0110).AsSByte()).AsByte();
                output = Vector256.Shuffle(output, Vector256.Create((byte)0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destRef, offset / 2), output.AsUInt64().GetLower());
            }

            return true;
        }

        /// <summary>
        /// Loops in 64 byte chunks and decodes them into 32 byte chunks.
        /// Unrolled 2x to process 128 hex bytes per iteration when possible.
        /// </summary>
        [SkipLocalsInit]
        internal static bool TryDecodeFromUtf8_Vector512(ReadOnlySpan<byte> hex, Span<byte> bytes)
        {
            Debug.Assert(Avx512BW.IsSupported);
            Debug.Assert((hex.Length / 2) + (hex.Length % 2) == bytes.Length);
            Debug.Assert(hex.Length >= Vector512<byte>.Count);

            nuint offset = 0;
            nuint hexLength = (nuint)hex.Length;

            ref byte srcRef = ref MemoryMarshal.GetReference(hex);
            ref byte destRef = ref MemoryMarshal.GetReference(bytes);

            Vector512<byte> shuf = Vector512.Create((byte)0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 32, 34, 36, 38, 40, 42, 44, 46, 48, 50, 52, 54, 56, 58, 60, 62, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

            // 2x unrolled loop: process 128 hex bytes -> 64 output bytes per iteration
            while (offset + (nuint)Vector512<byte>.Count * 2 <= hexLength)
            {
                Vector512<byte> vec0 = Vector512.LoadUnsafe(ref srcRef, offset);
                Vector512<byte> vec1 = Vector512.LoadUnsafe(ref srcRef, offset + (nuint)Vector512<byte>.Count);

                // Process vec0
                Vector512<byte> t1_0 = vec0 + Vector512.Create((byte)(0xFF - '9'));
                Vector512<byte> t2_0 = Avx512BW.SubtractSaturate(t1_0, Vector512.Create((byte)6));
                Vector512<byte> t3_0 = (vec0 & Vector512.Create((byte)0xDF)) - Vector512.Create((byte)'A');
                Vector512<byte> t4_0 = Avx512BW.AddSaturate(t3_0, Vector512.Create((byte)10));
                Vector512<byte> nibbles0 = Vector512.Min(t2_0 - Vector512.Create((byte)0xF0), t4_0);

                // Process vec1
                Vector512<byte> t1_1 = vec1 + Vector512.Create((byte)(0xFF - '9'));
                Vector512<byte> t2_1 = Avx512BW.SubtractSaturate(t1_1, Vector512.Create((byte)6));
                Vector512<byte> t3_1 = (vec1 & Vector512.Create((byte)0xDF)) - Vector512.Create((byte)'A');
                Vector512<byte> t4_1 = Avx512BW.AddSaturate(t3_1, Vector512.Create((byte)10));
                Vector512<byte> nibbles1 = Vector512.Min(t2_1 - Vector512.Create((byte)0xF0), t4_1);

                // Combined validity check
                Vector512<byte> invalid0 = (vec0 & Vector512.Create((byte)0x80)) | Avx512BW.AddSaturate(nibbles0, Vector512.Create((byte)(127 - 15)));
                Vector512<byte> invalid1 = (vec1 & Vector512.Create((byte)0x80)) | Avx512BW.AddSaturate(nibbles1, Vector512.Create((byte)(127 - 15)));
                if (((invalid0 | invalid1).ExtractMostSignificantBits()) != 0)
                    return false;

                // Convert and store
                Vector512<byte> output0 = Avx512BW.MultiplyAddAdjacent(nibbles0, Vector512.Create((short)0x0110).AsSByte()).AsByte();
                Vector512<byte> output1 = Avx512BW.MultiplyAddAdjacent(nibbles1, Vector512.Create((short)0x0110).AsSByte()).AsByte();
                output0 = Vector512.Shuffle(output0, shuf);
                output1 = Vector512.Shuffle(output1, shuf);

                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destRef, offset / 2), output0.AsUInt64().GetLower());
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destRef, offset / 2 + 32), output1.AsUInt64().GetLower());

                offset += (nuint)Vector512<byte>.Count * 2;
            }

            // Remainder: 1x loop for remaining 64-127 bytes
            while (offset + (nuint)Vector512<byte>.Count <= hexLength)
            {
                Vector512<byte> vec = Vector512.LoadUnsafe(ref srcRef, offset);

                Vector512<byte> t1 = vec + Vector512.Create((byte)(0xFF - '9'));
                Vector512<byte> t2 = Avx512BW.SubtractSaturate(t1, Vector512.Create((byte)6));
                Vector512<byte> t3 = (vec & Vector512.Create((byte)0xDF)) - Vector512.Create((byte)'A');
                Vector512<byte> t4 = Avx512BW.AddSaturate(t3, Vector512.Create((byte)10));
                Vector512<byte> nibbles = Vector512.Min(t2 - Vector512.Create((byte)0xF0), t4);

                Vector512<byte> invalid = (vec & Vector512.Create((byte)0x80)) | Avx512BW.AddSaturate(nibbles, Vector512.Create((byte)(127 - 15)));
                if (invalid.ExtractMostSignificantBits() != 0)
                    return false;

                Vector512<byte> output = Avx512BW.MultiplyAddAdjacent(nibbles, Vector512.Create((short)0x0110).AsSByte()).AsByte();
                output = Vector512.Shuffle(output, shuf);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destRef, offset / 2), output.AsUInt64().GetLower());

                offset += (nuint)Vector512<byte>.Count;
            }

            // Handle trailing < 64 bytes with overlap
            if (offset < hexLength)
            {
                offset = hexLength - (nuint)Vector512<byte>.Count;
                Vector512<byte> vec = Vector512.LoadUnsafe(ref srcRef, offset);

                Vector512<byte> t1 = vec + Vector512.Create((byte)(0xFF - '9'));
                Vector512<byte> t2 = Avx512BW.SubtractSaturate(t1, Vector512.Create((byte)6));
                Vector512<byte> t3 = (vec & Vector512.Create((byte)0xDF)) - Vector512.Create((byte)'A');
                Vector512<byte> t4 = Avx512BW.AddSaturate(t3, Vector512.Create((byte)10));
                Vector512<byte> nibbles = Vector512.Min(t2 - Vector512.Create((byte)0xF0), t4);

                Vector512<byte> invalid = (vec & Vector512.Create((byte)0x80)) | Avx512BW.AddSaturate(nibbles, Vector512.Create((byte)(127 - 15)));
                if (invalid.ExtractMostSignificantBits() != 0)
                    return false;

                Vector512<byte> output = Avx512BW.MultiplyAddAdjacent(nibbles, Vector512.Create((short)0x0110).AsSByte()).AsByte();
                output = Vector512.Shuffle(output, shuf);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destRef, offset / 2), output.AsUInt64().GetLower());
            }

            return true;
        }

        public static bool TryDecodeFromUtf16_Vector128(ReadOnlySpan<char> chars, Span<byte> bytes)
        {
            Debug.Assert(Ssse3.IsSupported || AdvSimd.Arm64.IsSupported);
            Debug.Assert(chars.Length <= bytes.Length * 2);
            Debug.Assert(chars.Length % 2 == 0);
            Debug.Assert(chars.Length >= Vector128<ushort>.Count * 2);

            nuint offset = 0;
            nuint lengthSubTwoVector128 = (nuint)chars.Length - ((nuint)Vector128<ushort>.Count * 2);

            ref ushort srcRef = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(chars));
            ref byte destRef = ref MemoryMarshal.GetReference(bytes);

            do
            {
                // The algorithm is UTF8 so we'll be loading two UTF-16 vectors to narrow them into a
                // single UTF8 ASCII vector - the implementation can be shared with UTF8 paths.
                Vector128<ushort> vec1 = Vector128.LoadUnsafe(ref srcRef, offset);
                Vector128<ushort> vec2 = Vector128.LoadUnsafe(ref srcRef, offset + (nuint)Vector128<ushort>.Count);
                Vector128<byte> vec = Vector128.Narrow(vec1, vec2);

                // Based on "Algorithm #3" https://github.com/WojciechMula/toys/blob/master/simd-parse-hex/geoff_algorithm.cpp
                // by Geoff Langdale and Wojciech Mula
                // Move digits '0'..'9' into range 0xf6..0xff.
                Vector128<byte> t1 = vec + Vector128.Create((byte)(0xFF - '9'));
                // And then correct the range to 0xf0..0xf9.
                // All other bytes become less than 0xf0.
                Vector128<byte> t2 = SubtractSaturate(t1, Vector128.Create((byte)6));
                // Convert into uppercase 'a'..'f' => 'A'..'F' and
                // move hex letter 'A'..'F' into range 0..5.
                Vector128<byte> t3 = (vec & Vector128.Create((byte)0xDF)) - Vector128.Create((byte)'A');
                // And correct the range into 10..15.
                // The non-hex letters bytes become greater than 0x0f.
                Vector128<byte> t4 = AddSaturate(t3, Vector128.Create((byte)10));
                // Convert '0'..'9' into nibbles 0..9. Non-digit bytes become
                // greater than 0x0f. Finally choose the result: either valid nibble (0..9/10..15)
                // or some byte greater than 0x0f.
                Vector128<byte> nibbles = Vector128.Min(t2 - Vector128.Create((byte)0xF0), t4);
                // Any high bit is a sign that input is not a valid hex data
                if (!AllCharsInVectorAreAscii(vec1 | vec2) ||
                    AddSaturate(nibbles, Vector128.Create((byte)(127 - 15))).ExtractMostSignificantBits() != 0)
                {
                    // Input is either non-ASCII or invalid hex data
                    break;
                }
                Vector128<byte> output;
                if (Ssse3.IsSupported)
                {
                    output = Ssse3.MultiplyAddAdjacent(nibbles,
                        Vector128.Create((short)0x0110).AsSByte()).AsByte();
                }
                else
                {
                    // Workaround for missing MultiplyAddAdjacent on ARM
                    Vector128<short> even = AdvSimd.Arm64.TransposeEven(nibbles, Vector128<byte>.Zero).AsInt16();
                    Vector128<short> odd = AdvSimd.Arm64.TransposeOdd(nibbles, Vector128<byte>.Zero).AsInt16();
                    even = AdvSimd.ShiftLeftLogical(even, 4).AsInt16();
                    output = AdvSimd.AddSaturate(even, odd).AsByte();
                }
                // Accumulate output in lower INT64 half and take care about endianness
                output = Vector128.Shuffle(output, Vector128.Create((byte)0, 2, 4, 6, 8, 10, 12, 14, 0, 0, 0, 0, 0, 0, 0, 0));
                // Store 8 bytes in dest by given offset
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destRef, offset / 2), output.AsUInt64().ToScalar());

                offset += (nuint)Vector128<ushort>.Count * 2;
                if (offset == (nuint)chars.Length)
                {
                    return true;
                }
                // Overlap with the current chunk for trailing elements
                if (offset > lengthSubTwoVector128)
                {
                    offset = lengthSubTwoVector128;
                }
            }
            while (true);

            // Fall back to the scalar routine in case of invalid input.
            return TryDecodeFromUtf16(chars[(int)offset..], bytes[(int)(offset / 2)..], isOdd: false);
        }

        /// <summary>
        /// Returns true iff the Vector128 represents 8 ASCII UTF-16 characters in machine endianness.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AllCharsInVectorAreAscii(Vector128<ushort> vec)
        {
            return (vec & Vector128.Create(unchecked((ushort)~0x007F))) == Vector128<ushort>.Zero;
        }

        /// <summary>
        /// Returns true iff the Vector128 represents 8 ASCII UTF-16 characters in machine endianness.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AllCharsInVectorAreAscii(Vector128<byte> vec)
        {
            return (vec & Vector128.Create(unchecked((byte)~0x7F))) == Vector128<byte>.Zero;
        }

        /// <summary>
        /// Returns true iff the Vector128 represents 8 ASCII UTF-16 characters in machine endianness.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AllCharsInVectorAreAscii(Vector256<byte> vec)
        {
            return (vec & Vector256.Create(unchecked((byte)~0x7F))) == Vector256<byte>.Zero;
        }

        /// <summary>
        /// Returns true iff the Vector128 represents 8 ASCII UTF-16 characters in machine endianness.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AllCharsInVectorAreAscii(Vector512<byte> vec)
        {
            return (vec & Vector512.Create(unchecked((byte)~0x7F))) == Vector512<byte>.Zero;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> AddSaturate(Vector128<byte> left, Vector128<byte> right)
        {
            if (Sse2.IsSupported)
            {
                return Sse2.AddSaturate(left, right);
            }
            else if (!AdvSimd.Arm64.IsSupported)
            {
                ThrowHelper.ThrowNotSupportedException();
            }
            return AdvSimd.AddSaturate(left, right);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> SubtractSaturate(Vector128<byte> left, Vector128<byte> right)
        {
            if (Sse2.IsSupported)
            {
                return Sse2.SubtractSaturate(left, right);
            }
            else if (!AdvSimd.Arm64.IsSupported)
            {
                ThrowHelper.ThrowNotSupportedException();
            }
            return AdvSimd.SubtractSaturate(left, right);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryDecodeFromUtf16(ReadOnlySpan<char> chars, Span<byte> bytes, bool isOdd)
        {
            Debug.Assert((chars.Length / 2) + (chars.Length % 2) == bytes.Length, "Target buffer not right-sized for provided characters");

            int i = 0;
            int j = 0;
            int byteLo = 0;
            int byteHi = 0;

            if (isOdd)
            {
                byteLo = FromChar(chars[i++]);
                bytes[j++] = (byte)byteLo;
            }

            while (j < bytes.Length)
            {
                byteLo = FromChar(chars[i + 1]);
                byteHi = FromChar(chars[i]);

                // byteHi hasn't been shifted to the high half yet, so the only way the bitwise or produces this pattern
                // is if either byteHi or byteLo was not a hex character.
                if ((byteLo | byteHi) == 0xFF)
                    break;

                bytes[j++] = (byte)((byteHi << 4) | byteLo);
                i += 2;
            }

            return (byteLo | byteHi) != 0xFF;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte FromChar(char c)
        {
            return c >= CharToHexLookup.Length ? (byte)0xFF : CharToHexLookup[c];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte FromUpperChar(char c)
        {
            return c > 71 ? (byte)0xFF : CharToHexLookup[c];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int FromLowerChar(int c)
        {
            if ((uint)(c - '0') <= '9' - '0')
                return c - '0';

            if ((uint)(c - 'a') <= 'f' - 'a')
                return c - 'a' + 10;

            return 0xFF;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsHexChar(char c)
        {
            if (IntPtr.Size == 8)
            {
                // This code path, when used, has no branches and doesn't depend on cache hits,
                // so it's faster and does not vary in speed depending on input data distribution.
                // We only use this logic on 64-bit systems, as using 64 bit values would otherwise
                // be much slower than just using the lookup table anyway (no hardware support).
                // The magic constant 18428868213665201664 is a 64 bit value containing 1s at the
                // indices corresponding to all the valid hex characters (ie. "0123456789ABCDEFabcdef")
                // minus 48 (ie. '0'), and backwards (so from the most significant bit and downwards).
                // The offset of 48 for each bit is necessary so that the entire range fits in 64 bits.
                // First, we subtract '0' to the input digit (after casting to uint to account for any
                // negative inputs). Note that even if this subtraction underflows, this happens before
                // the result is zero-extended to ulong, meaning that `i` will always have upper 32 bits
                // equal to 0. We then left shift the constant with this offset, and apply a bitmask that
                // has the highest bit set (the sign bit) if and only if `c` is in the ['0', '0' + 64) range.
                // Then we only need to check whether this final result is less than 0: this will only be
                // the case if both `i` was in fact the index of a set bit in the magic constant, and also
                // `c` was in the allowed range (this ensures that false positive bit shifts are ignored).
                ulong i = (uint)c - '0';
                ulong shift = 18428868213665201664UL << (int)i;
                ulong mask = i - 64;

                return (long)(shift & mask) < 0;
            }

            return FromChar(c) != 0xFF;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsHexUpperChar(int c)
        {
            return (uint)(c - '0') <= 9 || (uint)(c - 'A') <= ('F' - 'A');
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsHexLowerChar(int c)
        {
            return (uint)(c - '0') <= 9 || (uint)(c - 'a') <= ('f' - 'a');
        }

        /// <summary>Map from an ASCII char to its hex value, e.g. arr['b'] == 11. 0xFF means it's not a hex digit.</summary>
        public static ReadOnlySpan<byte> CharToHexLookup => new byte[]
        {
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // 15
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // 31
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // 47
            0x0,  0x1,  0x2,  0x3,  0x4,  0x5,  0x6,  0x7,  0x8,  0x9,  0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // 63
            0xFF, 0xA,  0xB,  0xC,  0xD,  0xE,  0xF,  0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // 79
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // 95
            0xFF, 0xa,  0xb,  0xc,  0xd,  0xe,  0xf,  0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // 111
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // 127
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // 143
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // 159
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // 175
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // 191
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // 207
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // 223
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // 239
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF  // 255
        };
    }
}
