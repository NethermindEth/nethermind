namespace Cortex.Containers
{
    public class Crosslink
    {
        public Crosslink(Shard shard)
        {
            Shard = shard;
            DataRoot = Hash32.Zero;
            ParentRoot = Hash32.Zero;
        }

        public Hash32 DataRoot { get; }
        public Epoch EndEpoch { get; }
        public Hash32 ParentRoot { get; }
        public Shard Shard { get; }
        public Epoch StartEpoch { get; }
    }
}
