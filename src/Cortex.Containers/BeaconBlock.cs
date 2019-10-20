namespace Cortex.Containers
{
    public class BeaconBlock
    {
        public BeaconBlock(Hash32 genesisStateRoot)
        {
            StateRoot = genesisStateRoot;

            Body = null;
            ParentRoot = Hash32.Zero;
            Signature = new BlsSignature();
            Slot = new Slot(0);
        } 

        public BeaconBlock(Slot slot, BlsSignature randaoReveal)
        {
            Body = new BeaconBlockBody(randaoReveal);
            Slot = slot;

            ParentRoot = Hash32.Zero;
            Signature = new BlsSignature();
            StateRoot = Hash32.Zero;
        }

        public BeaconBlockBody? Body { get; }
        public Hash32 ParentRoot { get; }
        public BlsSignature Signature { get; }
        public Slot Slot { get; }
        public Hash32 StateRoot { get; }
    }
}
