namespace Cortex.Containers
{
    public class BeaconBlock
    {
        public BeaconBlock(Hash32 genesisStateRoot)
        {
            StateRoot = genesisStateRoot;

            Body = null;
            ParentRoot = new Hash32();
            Signature = new BlsSignature();
            Slot = 0;
        } 

        public BeaconBlock(Slot slot, BlsSignature randaoReveal)
        {
            Body = new BeaconBlockBody(randaoReveal);
            Slot = slot;

            ParentRoot = new Hash32();
            Signature = new BlsSignature();
            StateRoot = new Hash32();
        }

        public BeaconBlockBody? Body { get; }
        public Hash32 ParentRoot { get; }
        public BlsSignature Signature { get; }
        public Slot Slot { get; }
        public Hash32 StateRoot { get; }
    }
}
