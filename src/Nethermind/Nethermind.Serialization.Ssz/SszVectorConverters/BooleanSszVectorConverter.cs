// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;

namespace Nethermind.Serialization.Ssz.SszVectorConverters;

public sealed class BooleanSszVectorConverter : ISszVectorConverter<bool>
{
    public const int Length = sizeof(byte);

    private BooleanSszVectorConverter() { }

    public static bool FromSpan(ReadOnlySpan<byte> span) =>
        span[0] switch
        {
            0 => false,
            1 => true,
            byte value => throw new InvalidDataException($"SSZ bool must be 0 or 1, got {value}")
        };

    public static void ToSpan(Span<byte> span, bool value) => Ssz.Encode(span, value);
}
