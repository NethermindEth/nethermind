// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Core;

[SszBasicTypeConverter<UInt256>]
public static class UInt256SszBasicTypeConverter
{
    public const int Length = 32;

    public static UInt256 FromSpan(ReadOnlySpan<byte> span) => new(span);

    public static void FromSpan(ReadOnlySpan<byte> span, Span<UInt256> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = FromSpan(span.Slice(i * Length, Length));
        }
    }

    public static void ToSpan(Span<byte> span, UInt256 value) => value.ToLittleEndian(span);

    public static void ToSpan(Span<byte> span, ReadOnlySpan<UInt256> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            ToSpan(span.Slice(i * Length, Length), values[i]);
        }
    }

    public static void Feed(ref Merkleizer merkleizer, UInt256 value) => merkleizer.Feed(value);
}
