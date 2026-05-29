// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.Serialization.Ssz.SszBasicTypeConverters;

[SszBasicTypeConverter<long>]
public static class Int64SszBasicTypeConverter
{
    public const int Length = sizeof(long);

    public static long FromSpan(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadInt64LittleEndian(span);

    public static void FromSpan(ReadOnlySpan<byte> span, Span<long> values)
    {
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.Cast<byte, long>(span).CopyTo(values);
            return;
        }

        for (int i = 0; i < values.Length; i++)
        {
            values[i] = FromSpan(span.Slice(i * Length, Length));
        }
    }

    public static void ToSpan(Span<byte> span, long value) => BinaryPrimitives.WriteInt64LittleEndian(span, value);

    public static void ToSpan(Span<byte> span, ReadOnlySpan<long> values)
    {
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.AsBytes(values).CopyTo(span);
            return;
        }

        for (int i = 0; i < values.Length; i++)
        {
            ToSpan(span.Slice(i * Length, Length), values[i]);
        }
    }

    public static void Feed(ref Merkleizer merkleizer, long value) =>
        merkleizer.Feed(new UInt256(unchecked((ulong)value)));
}
