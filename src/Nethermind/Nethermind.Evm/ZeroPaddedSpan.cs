// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm
{
    public readonly ref struct ZeroPaddedSpan
    {
        public static ZeroPaddedSpan Empty => new(default, 0, PadDirection.Right);

        public ZeroPaddedSpan(ReadOnlySpan<byte> span, int paddingLength, PadDirection padDirection)
        {
            PadDirection = padDirection;
            Span = span;
            PaddingLength = paddingLength;
        }

        public readonly PadDirection PadDirection;
        public readonly ReadOnlySpan<byte> Span;
        public readonly int PaddingLength;
        public int Length => Span.Length + PaddingLength;

        /// <summary>
        /// Temporary to handle old invocations
        /// </summary>
        /// <returns></returns>
        public readonly byte[] ToArray()
        {
            byte[] result = new byte[Span.Length + PaddingLength];
            Span.CopyTo(result.AsSpan(PadDirection == PadDirection.Right ? 0 : PaddingLength, Span.Length));
            return result;
        }
    }

    public ref struct ZeroPaddedMemory
    {
        public static ZeroPaddedMemory Empty => new(Memory<byte>.Empty, 0, PadDirection.Right);

        public ZeroPaddedMemory(ReadOnlyMemory<byte> memory, int paddingLength, PadDirection padDirection)
        {
            PadDirection = padDirection;
            Memory = memory;
            PaddingLength = paddingLength;
        }

        public PadDirection PadDirection;
        public ReadOnlyMemory<byte> Memory;
        public int PaddingLength;
        public int Length => Memory.Length + PaddingLength;

        /// <summary>
        /// Temporary to handle old invocations
        /// </summary>
        /// <returns></returns>
        public readonly byte[] ToArray()
        {
            byte[] result = new byte[Memory.Length + PaddingLength];
            Memory.CopyTo(result.AsMemory().Slice(PadDirection == PadDirection.Right ? 0 : PaddingLength, Memory.Length));
            return result;
        }
    }
}
