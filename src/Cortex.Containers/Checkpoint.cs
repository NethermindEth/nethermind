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
            var hashCode = 2147294683;
            hashCode = hashCode * -1521134295 + Epoch.GetHashCode();
            hashCode = hashCode * -1521134295 + ByteArrayEqualityComparer.Default.GetHashCode(Root);
            //hashCode = hashCode * -1521134295 + EqualityComparer<byte[]>.Default.GetHashCode(Root);
            return hashCode;
        }
    }
}
