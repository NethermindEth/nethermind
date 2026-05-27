// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Int256;
using Nethermind.Merkleization;

namespace Nethermind.Serialization.Ssz.SszVectorConverters;

public sealed class UInt64SszVectorConverter : ISszVectorConverter<ulong>
{
    public const int Length = sizeof(ulong);

    private UInt64SszVectorConverter() { }

    public static ulong FromSpan(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadUInt64LittleEndian(span);

    public static void ToSpan(Span<byte> span, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(span, value);

    public static void Merkleize(ulong value, out UInt256 root) => Merkle.Merkleize(out root, value);
}
