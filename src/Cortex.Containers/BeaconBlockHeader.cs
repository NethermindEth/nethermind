using System;

namespace Cortex.Containers
{
    public class BeaconBlockHeader
    {
        public BeaconBlockHeader(Hash32 bodyRoot)
        {
            BodyRoot = bodyRoot;
            ParentRoot = Hash32.Zero;
            Signature = new BlsSignature();
            StateRoot = Hash32.Zero;
        }

        public Hash32 BodyRoot { get; }
        public Hash32 ParentRoot { get; }
        public BlsSignature Signature { get; }
        public Slot Slot { get; }
        public Hash32 StateRoot { get; private set; }

        public void SetStateRoot(Hash32 stateRoot)
        {
            StateRoot = stateRoot;
        }
    }
}
