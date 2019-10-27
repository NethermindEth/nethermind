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
