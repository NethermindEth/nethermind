// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.Serialization.Ssz.SszBasicTypeConverters;

[SszBasicTypeConverter<UInt128>]
public static class UInt128SszBasicTypeConverter
{
    public const int Length = 16;

    public static UInt128 FromSpan(ReadOnlySpan<byte> span)
    {
        ulong lower = BinaryPrimitives.ReadUInt64LittleEndian(span[..8]);
        ulong upper = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(8, 8));
        return new UInt128(upper, lower);
    }

    public static void FromSpan(ReadOnlySpan<byte> span, Span<UInt128> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = FromSpan(span.Slice(i * Length, Length));
        }
    }

    public static void ToSpan(Span<byte> span, UInt128 value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(span[..8], (ulong)(value & ulong.MaxValue));
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(8, 8), (ulong)(value >> 64));
    }

    public static void ToSpan(Span<byte> span, ReadOnlySpan<UInt128> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            ToSpan(span.Slice(i * Length, Length), values[i]);
        }
    }

    public static void Feed(ref Merkleizer merkleizer, UInt128 value) =>
        merkleizer.Feed(new UInt256((ulong)(value & ulong.MaxValue), (ulong)(value >> 64)));
}
