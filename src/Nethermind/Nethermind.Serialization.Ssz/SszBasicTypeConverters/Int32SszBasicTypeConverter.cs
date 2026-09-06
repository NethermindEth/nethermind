// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.Serialization.Ssz.SszBasicTypeConverters;

[SszBasicTypeConverter<int>]
public static class Int32SszBasicTypeConverter
{
    public const int Length = sizeof(int);

    public static int FromSpan(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadInt32LittleEndian(span);

    public static void FromSpan(ReadOnlySpan<byte> span, Span<int> values) =>
        MemoryMarshal.Cast<byte, int>(span).CopyTo(values);

    public static void ToSpan(Span<byte> span, int value) => BinaryPrimitives.WriteInt32LittleEndian(span, value);

    public static void ToSpan(Span<byte> span, ReadOnlySpan<int> values) =>
        MemoryMarshal.AsBytes(values).CopyTo(span);

    public static void Feed(ref Merkleizer merkleizer, int value) =>
        merkleizer.Feed(new UInt256(unchecked((uint)value)));
}
