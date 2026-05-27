// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;
using Nethermind.Merkleization;

namespace Nethermind.Serialization.Ssz.SszVectorConverters;

public sealed class ByteSszVectorConverter : ISszVectorConverter<byte>
{
    public const int Length = sizeof(byte);

    private ByteSszVectorConverter() { }

    public static byte FromSpan(ReadOnlySpan<byte> span) => span[0];

    public static void ToSpan(Span<byte> span, byte value) => span[0] = value;

    public static void Merkleize(byte value, out UInt256 root) => Merkle.Merkleize(out root, value);
}
