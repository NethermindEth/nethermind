// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Core.Crypto;

[SszVectorTypeConverter<Hash256>]
public static class Hash256SszVectorTypeConverter
{
    public const int Length = Hash256.Size;

    public static Hash256 FromSpan(ReadOnlySpan<byte> span) => new(span);

    public static void FromSpan(ReadOnlySpan<byte> span, Span<Hash256> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = FromSpan(span.Slice(i * Length, Length));
        }
    }

    public static void ToSpan(Span<byte> span, Hash256 value) => value.Bytes.CopyTo(span);

    public static void ToSpan(Span<byte> span, ReadOnlySpan<Hash256> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            ToSpan(span.Slice(i * Length, Length), values[i]);
        }
    }

    public static void Feed(ref Merkleizer merkleizer, Hash256 value)
    {
        Merkle.Merkleize(out UInt256 root, value.Bytes);
        merkleizer.Feed(root);
    }
}
