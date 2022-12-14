// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int ForkVersionLength = ForkVersion.Length;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ForkVersion DecodeForkVersion(ReadOnlySpan<byte> span, ref int offset)
        {
            ForkVersion forkVersion = new ForkVersion(span.Slice(offset, ForkVersionLength));
            offset += ForkVersionLength;
            return forkVersion;
        }

        public static ForkVersion DecodeForkVersion(ReadOnlySpan<byte> span)
        {
            return new ForkVersion(span.Slice(0, ForkVersionLength));
        }

        public static void Encode(Span<byte> span, ForkVersion value)
        {
            value.AsSpan().CopyTo(span);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, ForkVersion value, ref int offset)
        {
            // FIXME: ForkVersion can be created by marshalling a span onto it, with no guarantee the underlying architecture is little endian.
            value.AsSpan().CopyTo(span.Slice(offset, ForkVersionLength));
            offset += ForkVersionLength;
        }
    }
}
