// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using System;

namespace Nethermind.Xdc.Types;

public class SubnetSnapshot : Snapshot, IEquatable<SubnetSnapshot>
{
    public Address[] NextEpochPenalties { get; set; }

    public SubnetSnapshot(long number, Hash256 hash, Address[] validators) : base(number, hash, validators) => NextEpochPenalties = [];

    public SubnetSnapshot(long number, Hash256 hash, Address[] validators, Address[] penalties) : base(number, hash, validators) => NextEpochPenalties = penalties ?? [];

    public bool Equals(SubnetSnapshot? other) => Equals((Snapshot?)other);

    protected override bool EqualsCore(Snapshot other) =>
        other is SubnetSnapshot subnetSnapshot &&
        base.EqualsCore(other) &&
        NextEpochPenalties.AsSpan().SequenceEqual(subnetSnapshot.NextEpochPenalties);

    protected override void AddHashCodeComponents(ref HashCode hashCode)
    {
        Address[] nextEpochPenalties = NextEpochPenalties ?? [];
        base.AddHashCodeComponents(ref hashCode);
        hashCode.Add(nextEpochPenalties.Length);
        foreach (Address penalty in nextEpochPenalties)
        {
            hashCode.Add(penalty);
        }
    }
}
