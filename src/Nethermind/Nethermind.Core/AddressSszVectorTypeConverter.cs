// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Core;

[SszVectorTypeConverter<Address>]
public static class AddressSszVectorTypeConverter
{
    public const int Length = Address.Size;

    public static Address FromSpan(ReadOnlySpan<byte> span) => new(span);

    public static void FromSpan(ReadOnlySpan<byte> span, Span<Address> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = FromSpan(span.Slice(i * Length, Length));
        }
    }

    public static void ToSpan(Span<byte> span, Address value) => value.Bytes.CopyTo(span);

    public static void ToSpan(Span<byte> span, ReadOnlySpan<Address> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            ToSpan(span.Slice(i * Length, Length), values[i]);
        }
    }

    public static void Feed(ref Merkleizer merkleizer, Address value)
    {
        Merkle.Merkleize(out UInt256 root, value.Bytes);
        merkleizer.Feed(root);
    }
}
