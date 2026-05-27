// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Int256;
using Nethermind.Merkleization;

namespace Nethermind.Serialization.Ssz.SszVectorConverters;

public sealed class UInt32SszVectorConverter : ISszVectorConverter<uint>
{
    public const int Length = sizeof(uint);

    private UInt32SszVectorConverter() { }

    public static uint FromSpan(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadUInt32LittleEndian(span);

    public static void ToSpan(Span<byte> span, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(span, value);

    public static void Feed(ref Merkleizer merkleizer, uint value) => merkleizer.Feed(new UInt256(value));
}
