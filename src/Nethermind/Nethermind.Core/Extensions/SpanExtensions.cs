// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Nethermind.Core.Crypto;

namespace Nethermind.Core.Extensions
{
    public static class SpanExtensions
    {
        public static string ToHexString(this in ReadOnlySpan<byte> span, bool withZeroX)
        {
            return ToHexString(span, withZeroX, false, false);
        }

        public static string ToHexString(this in Span<byte> span, bool withZeroX)
        {
            return ToHexViaLookup(span, withZeroX, false, false);
        }

        public static string ToHexString(this in ReadOnlySpan<byte> span, bool withZeroX, bool noLeadingZeros)
        {
            return ToHexViaLookup(span, withZeroX, noLeadingZeros, false);
        }

        public static string ToHexString(this in ReadOnlySpan<byte> span)
        {
            return ToHexString(span, false, false, false);
        }

        public static string ToHexString(this in Span<byte> span)
        {
            return ToHexViaLookup(span, false, false, false);
        }

        public static string ToHexString(this in ReadOnlySpan<byte> span, bool withZeroX, bool noLeadingZeros, bool withEip55Checksum)
        {
            return ToHexViaLookup(span, withZeroX, noLeadingZeros, withEip55Checksum);
        }

        public static string ToHexString(this in Span<byte> span, bool withZeroX, bool noLeadingZeros, bool withEip55Checksum)
        {
            return ToHexViaLookup(span, withZeroX, noLeadingZeros, withEip55Checksum);
        }

        [DebuggerStepThrough]
        private static string ToHexViaLookup(ReadOnlySpan<byte> bytes, bool withZeroX, bool skipLeadingZeros, bool withEip55Checksum)
        {
            if (bytes.Length == 0)
            {
                return withZeroX ? "0x" : "";
            }

            if (withEip55Checksum)
            {
                return ToHexStringWithEip55Checksum(bytes, withZeroX, skipLeadingZeros);
            }

            int leadingZeros = skipLeadingZeros ? Bytes.CountLeadingZeros(bytes) : 0;
            int length = bytes.Length * 2 + (withZeroX ? 2 : 0) - leadingZeros;

            char[] charArray = ArrayPool<char>.Shared.Rent(length);
            ref byte input = ref Unsafe.Add(ref MemoryMarshal.GetReference(bytes), leadingZeros / 2);
            Bytes.OutputBytesToCharHex(ref input, bytes.Length, ref charArray[0], withZeroX, leadingZeros);
            string result = new(charArray.AsSpan(0, length));
            ArrayPool<char>.Shared.Return(charArray);

            return result;
        }

        private static string ToHexStringWithEip55Checksum(ReadOnlySpan<byte> bytes, bool withZeroX, bool skipLeadingZeros)
        {
            string hashHex = Keccak.Compute(bytes.ToHexString(false)).ToString(false);

            int leadingZeros = skipLeadingZeros ? Bytes.CountLeadingZeros(bytes) : 0;
            int length = bytes.Length * 2 + (withZeroX ? 2 : 0) - leadingZeros;
            if (leadingZeros >= 2)
            {
                bytes = bytes[(leadingZeros / 2)..];
            }
            char[] charArray = ArrayPool<char>.Shared.Rent(length);

            Span<char> chars = charArray.AsSpan(0, length);

            if (withZeroX)
            {
                // In reverse, bounds check on [1] will elide bounds check on [0]
                chars[1] = 'x';
                chars[0] = '0';
                // Trim off the two chars from the span
                chars = chars[2..];
            }

            uint[] lookup32 = Bytes.Lookup32;

            bool odd = (leadingZeros & 1) != 0;
            int oddity = odd ? 2 : 0;
            if (odd)
            {
                // Odd number of hex chars, handle the first
                // seperately so loop can work in pairs
                uint val = lookup32[bytes[0]];
                char char2 = (char)(val >> 16);
                chars[0] = char.IsLetter(char2) && hashHex[1] > '7'
                            ? char.ToUpper(char2)
                            : char2;

                // Trim off the first byte and char from the spans
                chars = chars[1..];
                bytes = bytes[1..];
            }

            for (int i = 0; i < chars.Length; i += 2)
            {
                uint val = lookup32[bytes[i / 2]];
                char char1 = (char)val;
                char char2 = (char)(val >> 16);

                chars[i] = char.IsLetter(char1) && hashHex[i + oddity] > '7'
                            ? char.ToUpper(char1)
                            : char1;
                chars[i + 1] = char.IsLetter(char2) && hashHex[i + 1 + oddity] > '7'
                            ? char.ToUpper(char2)
                            : char2;
            }

            string result = new string(charArray.AsSpan(0, length));
            ArrayPool<char>.Shared.Return(charArray);

            return result;
        }

        public static bool IsNullOrEmpty<T>(this in Span<T> span) => span.Length == 0;
        public static bool IsNull<T>(this in Span<T> span) => Unsafe.IsNullRef(ref MemoryMarshal.GetReference(span));
        public static bool IsNullOrEmpty<T>(this in ReadOnlySpan<T> span) => span.Length == 0;
        public static bool IsNull<T>(this in ReadOnlySpan<T> span) => Unsafe.IsNullRef(ref MemoryMarshal.GetReference(span));
    }
}
