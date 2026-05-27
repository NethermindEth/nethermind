// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Merkleization;

namespace Nethermind.Serialization.Ssz.SszVectorConverters;

public sealed class UInt16SszVectorConverter : ISszVectorConverter<ushort>
{
    public const int Length = sizeof(ushort);

    private UInt16SszVectorConverter() { }

    public static ushort FromSpan(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadUInt16LittleEndian(span);

    public static void ToSpan(Span<byte> span, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(span, value);

    public static void Feed(ref Merkleizer merkleizer, ushort value) => merkleizer.Feed(value);
}
