// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.Serialization.Ssz.SszBasicTypeConverters;

[SszBasicTypeConverter<uint>]
public static class UInt32SszBasicTypeConverter
{
    public const int Length = sizeof(uint);

    public static uint FromSpan(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadUInt32LittleEndian(span);

    public static void FromSpan(ReadOnlySpan<byte> span, Span<uint> values)
    {
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.Cast<byte, uint>(span).CopyTo(values);
            return;
        }

        for (int i = 0; i < values.Length; i++)
        {
            values[i] = FromSpan(span.Slice(i * Length, Length));
        }
    }

    public static void ToSpan(Span<byte> span, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(span, value);

    public static void ToSpan(Span<byte> span, ReadOnlySpan<uint> values)
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

    public static void Feed(ref Merkleizer merkleizer, uint value) => merkleizer.Feed(new UInt256(value));
}
