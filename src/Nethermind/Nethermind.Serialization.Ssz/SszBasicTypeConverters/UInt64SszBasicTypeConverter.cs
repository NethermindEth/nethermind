// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.Serialization.Ssz.SszBasicTypeConverters;

[SszBasicTypeConverter<ulong>]
public static class UInt64SszBasicTypeConverter
{
    public const int Length = sizeof(ulong);

    public static ulong FromSpan(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadUInt64LittleEndian(span);

    public static void FromSpan(ReadOnlySpan<byte> span, Span<ulong> values) =>
        MemoryMarshal.Cast<byte, ulong>(span).CopyTo(values);

    public static void ToSpan(Span<byte> span, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(span, value);

    public static void ToSpan(Span<byte> span, ReadOnlySpan<ulong> values) =>
        MemoryMarshal.AsBytes(values).CopyTo(span);

    public static void Feed(ref Merkleizer merkleizer, ulong value) => merkleizer.Feed(new UInt256(value));
}
