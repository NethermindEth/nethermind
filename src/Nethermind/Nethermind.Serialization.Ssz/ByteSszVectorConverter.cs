// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Ssz;

public sealed class ByteSszVectorConverter : SszVectorConverter<byte>
{
    public const int Length = sizeof(byte);

    private ByteSszVectorConverter() { }

    public static byte FromSpan(ReadOnlySpan<byte> span) => Ssz.DecodeByte(span);

    public static void ToSpan(Span<byte> span, byte value) => Ssz.Encode(span, value);
}
