// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest;

[SszVectorTypeConverter<SszKzgCommitment>]
public static class SszKzgCommitmentVectorTypeConverter
{
    public const int Length = SszKzgCommitment.KzgCommitmentLength;

    public static SszKzgCommitment FromSpan(ReadOnlySpan<byte> span) => SszKzgCommitment.FromSpan(span);

    public static void FromSpan(ReadOnlySpan<byte> span, Span<SszKzgCommitment> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = FromSpan(span.Slice(i * Length, Length));
        }
    }

    public static void ToSpan(Span<byte> span, SszKzgCommitment value) => value.AsSpan().CopyTo(span);

    public static void ToSpan(Span<byte> span, ReadOnlySpan<SszKzgCommitment> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            ToSpan(span.Slice(i * Length, Length), values[i]);
        }
    }

    public static void Feed(ref Merkleizer merkleizer, SszKzgCommitment value)
    {
        Merkle.Merkleize(out UInt256 root, value.AsSpan());
        merkleizer.Feed(root);
    }
}
