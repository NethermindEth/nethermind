// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.Serialization.Ssz.SszBasicTypeConverters;

[SszBasicTypeConverter<bool>]
public static class BooleanSszBasicTypeConverter
{
    public const int Length = sizeof(byte);

    public static bool FromSpan(ReadOnlySpan<byte> span) =>
        span[0] switch
        {
            0 => false,
            1 => true,
            byte value => throw new InvalidDataException($"SSZ bool must be 0 or 1, got {value}")
        };

    public static void FromSpan(ReadOnlySpan<byte> span, Span<bool> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = span[i] switch
            {
                0 => false,
                1 => true,
                byte value => throw new InvalidDataException($"SSZ bool must be 0 or 1, got {value}")
            };
        }
    }

    public static void ToSpan(Span<byte> span, bool value) => span[0] = value ? (byte)1 : (byte)0;

    public static void ToSpan(Span<byte> span, ReadOnlySpan<bool> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            span[i] = values[i] ? (byte)1 : (byte)0;
        }
    }

    public static void Feed(ref Merkleizer merkleizer, bool value) => merkleizer.Feed(value ? UInt256.One : UInt256.Zero);
}
