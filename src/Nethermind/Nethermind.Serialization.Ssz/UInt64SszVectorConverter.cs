// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Ssz;

public sealed class UInt64SszVectorConverter : SszVectorConverter<ulong>
{
    public const int Length = sizeof(ulong);

    private UInt64SszVectorConverter() { }

    public static ulong FromSpan(ReadOnlySpan<byte> span) => Ssz.DecodeULong(span);

    public static void ToSpan(Span<byte> span, ulong value) => Ssz.Encode(span, value);
}
