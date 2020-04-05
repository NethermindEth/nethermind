//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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