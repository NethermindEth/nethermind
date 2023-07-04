// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

using Nethermind.Int256;

namespace Nethermind.Evm
{
    public static class ByteArrayExtensions
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ZeroPaddedSpan SliceWithZeroPadding(this Span<byte> span, int startIndex, int length, PadDirection padDirection)
            => ((ReadOnlySpan<byte>)span).SliceWithZeroPadding(startIndex, length, padDirection);

        private static ZeroPaddedSpan SliceWithZeroPadding(this ReadOnlySpan<byte> span, int startIndex, int length, PadDirection padDirection)
        {
            if (startIndex >= span.Length)
            {
                return new ZeroPaddedSpan(default, length, padDirection);
            }

            if (length == 1)
            {
                // why do we return zero length here?
                // it was passing all the tests like this...
                // return bytes.Length == 0 ? new byte[0] : new[] {bytes[startIndex]};
                return span.Length == 0 ? new ZeroPaddedSpan(default, 0, padDirection) : new ZeroPaddedSpan(span.Slice(startIndex, 1), 0, padDirection);
                // return bytes.Length == 0 ? new ZeroPaddedSpan(default, 1) : new ZeroPaddedSpan(bytes.Slice(startIndex, 1), 0);
            }

            int copiedLength = Math.Min(span.Length - startIndex, length);
            return new ZeroPaddedSpan(span.Slice(startIndex, copiedLength), length - copiedLength, padDirection);
        }

        public static ZeroPaddedSpan SliceWithZeroPadding(this Span<byte> span, scoped in UInt256 startIndex, int length, PadDirection padDirection = PadDirection.Right)
        {
            if (startIndex >= span.Length || startIndex > int.MaxValue)
            {
                return new ZeroPaddedSpan(default, length, PadDirection.Right);
            }

            return SliceWithZeroPadding(span, (int)startIndex, length, padDirection);
        }

        public static ZeroPaddedSpan SliceWithZeroPadding(this ReadOnlyMemory<byte> bytes, scoped in UInt256 startIndex, int length, PadDirection padDirection = PadDirection.Right)
        {
            if (startIndex >= bytes.Length || startIndex > int.MaxValue)
            {
                return new ZeroPaddedSpan(default, length, PadDirection.Right);
            }

            return SliceWithZeroPadding(bytes.Span, (int)startIndex, length, padDirection);
        }

        public static ZeroPaddedSpan SliceWithZeroPadding(this byte[] bytes, scoped in UInt256 startIndex, int length, PadDirection padDirection = PadDirection.Right) =>
            bytes.AsSpan().SliceWithZeroPadding(startIndex, length, padDirection);

        public static ZeroPaddedSpan SliceWithZeroPadding(this byte[] bytes, int startIndex, int length, PadDirection padDirection = PadDirection.Right) =>
            bytes.AsSpan().SliceWithZeroPadding(startIndex, length, padDirection);
    }
}
