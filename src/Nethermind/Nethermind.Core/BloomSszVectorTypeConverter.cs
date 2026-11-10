// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Core;

[SszVectorTypeConverter<Bloom>]
public static class BloomSszVectorTypeConverter
{
    public const int Length = Bloom.ByteLength;

    public static Bloom FromSpan(ReadOnlySpan<byte> span) => new(span);

    public static void FromSpan(ReadOnlySpan<byte> span, Span<Bloom> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = FromSpan(span.Slice(i * Length, Length));
        }
    }

    /// <remarks>
    /// The SSZ vector is fixed at 256 bytes and has no EIP-7668 form yet, so <see cref="Bloom.ZeroLength"/> is written as
    /// zeros rather than leaving the pooled buffer's contents in place.
    /// </remarks>
    public static void ToSpan(Span<byte> span, Bloom value)
    {
        if (value.IsZeroLength) span[..Length].Clear();
        else value.Bytes.CopyTo(span);
    }

    public static void ToSpan(Span<byte> span, ReadOnlySpan<Bloom> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            ToSpan(span.Slice(i * Length, Length), values[i]);
        }
    }

    public static void Feed(ref Merkleizer merkleizer, Bloom value)
    {
        Merkle.Merkleize(out UInt256 root, value.IsZeroLength ? Bloom.Empty.Bytes : value.Bytes);
        merkleizer.Feed(root);
    }
}
