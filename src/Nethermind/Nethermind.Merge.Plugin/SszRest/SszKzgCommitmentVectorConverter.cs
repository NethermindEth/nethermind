// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;
using Nethermind.Merkleization;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest;

public sealed class SszKzgCommitmentVectorConverter : ISszVectorConverter<SszKzgCommitment>
{
    public const int Length = SszKzgCommitment.KzgCommitmentLength;

    private SszKzgCommitmentVectorConverter() { }

    public static SszKzgCommitment FromSpan(ReadOnlySpan<byte> span) => SszKzgCommitment.FromSpan(span);

    public static void ToSpan(Span<byte> span, SszKzgCommitment value) => value.AsSpan().CopyTo(span);

    public static void Feed(ref Merkleizer merkleizer, SszKzgCommitment value)
    {
        Merkle.Merkleize(out UInt256 root, value.AsSpan());
        merkleizer.Feed(root);
    }
}
