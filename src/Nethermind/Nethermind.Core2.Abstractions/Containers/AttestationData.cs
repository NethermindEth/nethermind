// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class AttestationData : IEquatable<AttestationData>
    {
        public static readonly AttestationData Zero = new AttestationData(Slot.Zero, CommitteeIndex.Zero, Root.Zero,
            Checkpoint.Zero, Checkpoint.Zero);

        public AttestationData(
            Slot slot,
            CommitteeIndex index,
            Root beaconBlockRoot,
            Checkpoint source,
            Checkpoint target)
        {
            BeaconBlockRoot = beaconBlockRoot;
            Source = source;
            Target = target;
            Slot = slot;
            Index = index;
        }

        public Root BeaconBlockRoot { get; }
        public CommitteeIndex Index { get; }
        public Slot Slot { get; }
        public Checkpoint Source { get; }
        public Checkpoint Target { get; }

        public static AttestationData Clone(AttestationData other)
        {
            var clone = new AttestationData(
                other.Slot,
                other.Index,
                other.BeaconBlockRoot,
                Checkpoint.Clone(other.Source),
                Checkpoint.Clone(other.Target)
            );
            return clone;
        }

        public bool Equals(AttestationData? other)
        {
            return !(other is null)
                   && BeaconBlockRoot.Equals(other.BeaconBlockRoot)
                   && Index == other.Index
                   && Slot == other.Slot
                   && Source.Equals(other.Source)
                   && Target.Equals(other.Target);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return Equals(obj as AttestationData);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(BeaconBlockRoot, Index, Slot, Source, Target);
        }

        public override string ToString()
        {
            return $"s={Slot}_i={Index}";
        }
    }
}
