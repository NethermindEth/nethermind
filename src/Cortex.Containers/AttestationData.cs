namespace Cortex.Containers
{
    public class AttestationData
    {
        public AttestationData(Hash32 beaconBlockRoot, Checkpoint source, Checkpoint target, Crosslink crosslink)
        {
            BeaconBlockRoot = beaconBlockRoot;
            Source = source;
            Target = target;
            Crosslink = crosslink;
        }

        public Hash32 BeaconBlockRoot { get; }
        public Crosslink Crosslink { get; }
        public Checkpoint Source { get; }
        public Checkpoint Target { get; }
    }
}
