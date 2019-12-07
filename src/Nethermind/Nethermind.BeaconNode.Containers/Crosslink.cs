using System;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers
{
    public class Crosslink : IEquatable<Crosslink>
    {
        public Crosslink(Shard shard)
            : this(shard, Hash32.Zero, Epoch.Zero, Epoch.Zero, Hash32.Zero)
        {
        }

        public Crosslink(Shard shard,
            Hash32 parentRoot,
            Epoch startEpoch,
            Epoch endEpoch,
            Hash32 dataRoot)
        {
            Shard = shard;
            ParentRoot = parentRoot;
            StartEpoch = startEpoch;
            EndEpoch = endEpoch;
            DataRoot = dataRoot;
        }

        public Hash32 DataRoot { get; }
        public Epoch EndEpoch { get; }
        public Hash32 ParentRoot { get; }
        public Shard Shard { get; }
        public Epoch StartEpoch { get; }

        /// <summary>
        /// Creates a deep copy of the object.
        /// </summary>
        public static Crosslink Clone(Crosslink other)
        {
            var clone = new Crosslink(
                other.Shard,
                other.ParentRoot,
                other.StartEpoch,
                other.EndEpoch,
                other.DataRoot
                );
            return clone;
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as Crosslink);
        }

        public bool Equals(Crosslink? other)
        {
            return !(other is null)
                && Shard == other.Shard
                && StartEpoch == other.StartEpoch
                && EndEpoch == other.EndEpoch
                && DataRoot.Equals(other.DataRoot)
                && ParentRoot.Equals(other.ParentRoot);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(DataRoot, EndEpoch, ParentRoot, Shard, StartEpoch);
        }
    }
}
