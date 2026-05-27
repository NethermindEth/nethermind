// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Merkleization;

namespace Nethermind.Serialization.Ssz.SszVectorConverters;

public sealed class ByteSszVectorConverter : ISszVectorConverter<byte>
{
    public const int Length = sizeof(byte);

    private ByteSszVectorConverter() { }

    public static byte FromSpan(ReadOnlySpan<byte> span) => span[0];

    public static void ToSpan(Span<byte> span, byte value) => span[0] = value;

    public static void Feed(ref Merkleizer merkleizer, byte value) => merkleizer.Feed(value);
}
