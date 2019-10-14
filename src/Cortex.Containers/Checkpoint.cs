using System;

namespace Cortex.Containers
{
    public class Checkpoint : IEquatable<Checkpoint>
    {
        public Checkpoint()
        {
            Root = new Hash32();
        }

        public Epoch Epoch { get; }

        public Hash32 Root { get; }

        public override bool Equals(object obj)
        {
            return Equals(obj as Checkpoint);
        }

        public bool Equals(Checkpoint? other)
        {
            return other != null &&
                   Epoch == other.Epoch &&
                   Root.Equals(other.Root);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Epoch, Root);
        }
    }
}
