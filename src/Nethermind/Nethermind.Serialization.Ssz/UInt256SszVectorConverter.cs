// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz;

public sealed class UInt256SszVectorConverter : SszVectorConverter<UInt256>
{
    public const int Length = 32;

    private UInt256SszVectorConverter() { }

    public static UInt256 FromSpan(ReadOnlySpan<byte> span) => Ssz.DecodeUInt256(span);

    public static void ToSpan(Span<byte> span, UInt256 value) => Ssz.Encode(span, value);
}
