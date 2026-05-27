// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using System;

namespace Nethermind.Xdc.Types;

public class Snapshot(long number, Hash256 hash, Address[] masterNodes) : IEquatable<Snapshot>
{
    public long BlockNumber { get; set; } = number;
    public Hash256 HeaderHash { get; set; } = hash;
    public Address[] NextEpochCandidates { get; set; } = masterNodes ?? [];

    public bool Equals(Snapshot? other) =>
        other is not null &&
        other.GetType() == GetType() &&
        EqualsCore(other);

    public override bool Equals(object? obj) => Equals(obj as Snapshot);

    public override int GetHashCode()
    {
        HashCode hashCode = new();
        AddHashCodeComponents(ref hashCode);
        return hashCode.ToHashCode();
    }

    protected virtual bool EqualsCore(Snapshot other) =>
        BlockNumber == other.BlockNumber &&
        HeaderHash == other.HeaderHash &&
        NextEpochCandidates.AsSpan().SequenceEqual(other.NextEpochCandidates);

    protected virtual void AddHashCodeComponents(ref HashCode hashCode)
    {
        Address[] nextEpochCandidates = NextEpochCandidates ?? [];
        hashCode.Add(BlockNumber);
        hashCode.Add(HeaderHash);
        hashCode.Add(nextEpochCandidates.Length);
        foreach (Address candidate in nextEpochCandidates)
        {
            hashCode.Add(candidate);
        }
    }
}
