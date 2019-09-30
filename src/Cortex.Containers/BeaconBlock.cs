using BlsSignature = System.Byte; // Byte96

using Hash = System.Byte; // Byte32

using Slot = System.UInt64;

namespace Cortex.Containers
{
    public class BeaconBlock
    {
        public BeaconBlock(Slot slot, BlsSignature[] randaoReveal)
        {
            Slot = slot;
            Body = new BeaconBlockBody(randaoReveal);
        }

        public BeaconBlockBody Body { get; }
        public Hash[] ParentRoot { get; }
        public BlsSignature[] Signature { get; }
        public Slot Slot { get; }
        public Hash[] StateRoot { get; }
    }
}
