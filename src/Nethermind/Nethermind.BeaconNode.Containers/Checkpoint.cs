using System;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers
{
    public class Checkpoint : IEquatable<Checkpoint>
    {
        public Checkpoint(Epoch epoch, Hash32 root)
        {
            Epoch = epoch;
            Root = root;
        }

        public Epoch Epoch { get; }

        public Hash32 Root { get; private set; }

        public static Checkpoint Clone(Checkpoint other)
        {
            var clone = new Checkpoint(
                other.Epoch,
                other.Root);
            return clone;
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as Checkpoint);
        }

        public bool Equals(Checkpoint? other)
        {
            return !(other is null)
                && Epoch == other.Epoch
                && Root.Equals(other.Root);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Epoch, Root);
        }

        public void SetRoot(Hash32 root)
        {
            Root = root;
        }

        public override string ToString()
        {
            return $"{Epoch}:{Root.ToString().Substring(0, 12)}";
        }
    }
}
