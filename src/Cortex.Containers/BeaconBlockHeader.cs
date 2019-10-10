namespace Cortex.Containers
{
    public class BeaconBlockHeader
    {
        public BeaconBlockHeader(Hash32 bodyRoot)
        {
            BodyRoot = bodyRoot;
        }

        public Hash32 BodyRoot { get; }
        public Hash32 ParentRoot { get; }
        public BlsSignature Signature { get; }
        public Slot Slot { get; }
        public Hash32 StateRoot { get; }
    }
}
