// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest;

[SszVectorTypeConverter<SszBlobCell>]
public static class SszBlobCellVectorTypeConverter
{
    public const int Length = SszBlobCell.BlobCellLength;

    public static SszBlobCell FromSpan(ReadOnlySpan<byte> span) => SszBlobCell.FromSpan(span);

    public static void FromSpan(ReadOnlySpan<byte> span, Span<SszBlobCell> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = FromSpan(span.Slice(i * Length, Length));
        }
    }

    public static void ToSpan(Span<byte> span, SszBlobCell value) => value.AsSpan().CopyTo(span);

    public static void ToSpan(Span<byte> span, ReadOnlySpan<SszBlobCell> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            ToSpan(span.Slice(i * Length, Length), values[i]);
        }
    }

    public static void Feed(ref Merkleizer merkleizer, SszBlobCell value)
    {
        Merkle.Merkleize(out UInt256 root, value.AsSpan());
        merkleizer.Feed(root);
    }
}
