namespace Cortex.Containers
{
    public class BeaconBlock
    {
        public BeaconBlock(Slot slot, BlsSignature randaoReveal)
        {
            Slot = slot;
            Body = new BeaconBlockBody(randaoReveal);
            ParentRoot = new Hash32();
            Signature = new BlsSignature();
            StateRoot = new Hash32();
        }

        public BeaconBlockBody Body { get; }
        public Hash32 ParentRoot { get; }
        public BlsSignature Signature { get; }
        public Slot Slot { get; }
        public Hash32 StateRoot { get; }
    }
}
