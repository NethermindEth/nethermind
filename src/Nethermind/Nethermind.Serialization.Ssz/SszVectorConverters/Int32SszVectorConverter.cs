// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Int256;
using Nethermind.Merkleization;

namespace Nethermind.Serialization.Ssz.SszVectorConverters;

public sealed class Int32SszVectorConverter : ISszVectorConverter<int>
{
    public const int Length = sizeof(int);

    private Int32SszVectorConverter() { }

    public static int FromSpan(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadInt32LittleEndian(span);

    public static void ToSpan(Span<byte> span, int value) => BinaryPrimitives.WriteInt32LittleEndian(span, value);

    public static void Feed(ref Merkleizer merkleizer, int value)
    {
        ulong signExtension = value < 0 ? ulong.MaxValue : 0UL;
        merkleizer.Feed(new UInt256(unchecked((ulong)value), signExtension, signExtension, signExtension));
    }
}
