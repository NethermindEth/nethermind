namespace Cortex.Containers
{
    public class AttestationData
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
                Hash32.Clone(other.BeaconBlockRoot),
                Checkpoint.Clone(other.Source),
                Checkpoint.Clone(other.Target)
                );
            return clone;
        }
    }
}
