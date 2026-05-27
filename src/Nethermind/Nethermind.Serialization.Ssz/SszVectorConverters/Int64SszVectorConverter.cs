// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Merkleization;

namespace Nethermind.Serialization.Ssz.SszVectorConverters;

public sealed class Int64SszVectorConverter : ISszVectorConverter<long>
{
    public const int Length = sizeof(long);

    private Int64SszVectorConverter() { }

    public static long FromSpan(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadInt64LittleEndian(span);

    public static void ToSpan(Span<byte> span, long value) => BinaryPrimitives.WriteInt64LittleEndian(span, value);

    public static void Feed(ref Merkleizer merkleizer, long value) => merkleizer.Feed(value);
}
