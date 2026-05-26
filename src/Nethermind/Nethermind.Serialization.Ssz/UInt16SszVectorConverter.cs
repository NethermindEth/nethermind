// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;

namespace Nethermind.Serialization.Ssz;

public sealed class UInt16SszVectorConverter : SszVectorConverter<ushort>
{
    public const int Length = sizeof(ushort);

    private UInt16SszVectorConverter() { }

    public static ushort FromSpan(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadUInt16LittleEndian(span);

    public static void ToSpan(Span<byte> span, ushort value) => Ssz.Encode(span, value);
}
