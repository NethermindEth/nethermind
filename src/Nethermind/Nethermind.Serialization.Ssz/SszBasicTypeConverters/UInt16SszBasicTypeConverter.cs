// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.Serialization.Ssz.SszBasicTypeConverters;

[SszBasicTypeConverter<ushort>]
public static class UInt16SszBasicTypeConverter
{
    public const int Length = sizeof(ushort);

    public static ushort FromSpan(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadUInt16LittleEndian(span);

    public static void FromSpan(ReadOnlySpan<byte> span, Span<ushort> values)
    {
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.Cast<byte, ushort>(span).CopyTo(values);
            return;
        }

        for (int i = 0; i < values.Length; i++)
        {
            values[i] = FromSpan(span.Slice(i * Length, Length));
        }
    }

    public static void ToSpan(Span<byte> span, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(span, value);

    public static void ToSpan(Span<byte> span, ReadOnlySpan<ushort> values)
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

    public static void Feed(ref Merkleizer merkleizer, ushort value) => merkleizer.Feed(new UInt256(value));
}
