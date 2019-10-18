namespace Cortex.Containers
{
    public class Crosslink
    {
        public Crosslink(Shard shard)
        {
            Shard = shard;
        }

        public Hash32 DataRoot { get; }
        public Epoch EndEpoch { get; }
        public Hash32 ParentRoot { get; }
        public Shard Shard { get; }
        public Epoch StartEpoch { get; }
    }
}
