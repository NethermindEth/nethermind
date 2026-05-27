// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;
using Nethermind.Merkleization;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Core;

public sealed class UInt256SszVectorConverter : ISszVectorConverter<UInt256>
{
    public const int Length = 32;

    private UInt256SszVectorConverter() { }

    public static UInt256 FromSpan(ReadOnlySpan<byte> span) => new(span);

    public static void ToSpan(Span<byte> span, UInt256 value) => value.ToLittleEndian(span);

    public static void Feed(ref Merkleizer merkleizer, UInt256 value) => merkleizer.Feed(value);
}
