namespace Cortex.Containers
{
    public class BeaconBlockHeader
    {
        public BeaconBlockHeader(
            Slot slot,
            Hash32 parentRoot,
            Hash32 stateRoot,
            Hash32 bodyRoot,
            BlsSignature signature)
        {
            Slot = slot;
            ParentRoot = parentRoot;
            StateRoot = stateRoot;
            BodyRoot = bodyRoot;
            Signature = signature;
        }

        public BeaconBlockHeader(Hash32 bodyRoot)
            : this (Slot.Zero, Hash32.Zero, Hash32.Zero, bodyRoot, new BlsSignature())
        {
        }

        public Hash32 BodyRoot { get; private set; }
        public Hash32 ParentRoot { get; private set; }
        public BlsSignature Signature { get; private set; }
        public Slot Slot { get; private set; }
        public Hash32 StateRoot { get; private set; }

        /// <summary>
        /// Creates a deep copy of the object.
        /// </summary>
        public static BeaconBlockHeader Clone(BeaconBlockHeader other)
        {
            var clone = new BeaconBlockHeader(Hash32.Clone(other.BodyRoot))
            {
                Slot = other.Slot,
                ParentRoot = Hash32.Clone(other.ParentRoot),
                StateRoot = Hash32.Clone(other.StateRoot),
                //BodyRoot = Hash32.Clone(other.BodyRoot),
                Signature = BlsSignature.Clone(other.Signature)
            };
            return clone;
        }

        public void SetStateRoot(Hash32 stateRoot)
        {
            StateRoot = stateRoot;
        }
    }
}
