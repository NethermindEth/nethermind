// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Ssz;

public sealed class UInt32SszVectorConverter : SszVectorConverter<uint>
{
    public const int Length = sizeof(uint);

    private UInt32SszVectorConverter() { }

    public static uint FromSpan(ReadOnlySpan<byte> span) => Ssz.DecodeUInt(span);

    public static void ToSpan(Span<byte> span, uint value) => Ssz.Encode(span, value);
}
