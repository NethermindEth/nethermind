using System;

namespace Cortex.Containers
{
    public class Crosslink : IEquatable<Crosslink>
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

        public override bool Equals(object obj)
        {
            return Equals(obj as Crosslink);
        }

        public bool Equals(Crosslink? other)
        {
            return other != null &&
                   Shard == other.Shard &&
                   StartEpoch == other.StartEpoch &&
                   EndEpoch == other.EndEpoch &&
                   DataRoot.Equals(other.DataRoot) &&
                   ParentRoot.Equals(other.ParentRoot);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(DataRoot, EndEpoch, ParentRoot, Shard, StartEpoch);
        }
    }
}
