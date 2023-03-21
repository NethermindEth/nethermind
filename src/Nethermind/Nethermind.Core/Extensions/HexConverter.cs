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

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryDecodeFromUtf8(ReadOnlySpan<byte> hex, Span<byte> bytes, bool isOdd)
        {
            Debug.Assert((hex.Length / 2) + (hex.Length % 2) == bytes.Length, "Target buffer not right-sized for provided characters");

            int i = 0;
            int j = 0;
            int byteLo = 0;
            int byteHi = 0;

            if (isOdd)
            {
                byteLo = FromChar((char)hex[i++]);
                bytes[j++] = (byte)byteLo;
            }

            while (j < bytes.Length)
            {
                byteLo = FromChar((char)hex[i + 1]);
                byteHi = FromChar((char)hex[i]);

                // byteHi hasn't been shifted to the high half yet, so the only way the bitwise or produces this pattern
                // is if either byteHi or byteLo was not a hex character.
                if ((byteLo | byteHi) == 0xFF)
                    break;

                bytes[j++] = (byte)((byteHi << 4) | byteLo);
                i += 2;
            }

            return (byteLo | byteHi) != 0xFF;
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
                if (!AllCharsInVector128AreAscii(vec1 | vec2) ||
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
        private static bool AllCharsInVector128AreAscii(Vector128<ushort> vec)
        {
            return (vec & Vector128.Create(unchecked((ushort)~0x007F))) == Vector128<ushort>.Zero;
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

                return (long)(shift & mask) < 0 ? true : false;
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
