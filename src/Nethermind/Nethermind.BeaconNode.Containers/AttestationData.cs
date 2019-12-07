using System;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers
{
    public class AttestationData : IEquatable<AttestationData>
    {
        public AttestationData(
            Slot slot,
            CommitteeIndex index,
            Hash32 beaconBlockRoot,
            Checkpoint source,
            Checkpoint target)
        {
            BeaconBlockRoot = beaconBlockRoot;
            Source = source;
            Target = target;
            Slot = slot;
            Index = index;
        }

        public Hash32 BeaconBlockRoot { get; }
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
            return Equals(obj as AttestationData);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(BeaconBlockRoot, Index, Slot, Source, Target);
        }
    }
}
