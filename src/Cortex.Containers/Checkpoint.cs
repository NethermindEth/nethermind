using System;
using System.Collections.Generic;
using Epoch = System.UInt64;
using Hash = System.Byte; // Byte32

namespace Cortex.Containers
{
    public class Checkpoint : IEquatable<Checkpoint>
    {
        public Epoch Epoch { get; }
        public Hash[] Root { get; }

        public override bool Equals(object obj)
        {
            return Equals(obj as Checkpoint);
        }

        public bool Equals(Checkpoint other)
        {
            return other != null &&
                   Epoch == other.Epoch &&
                   ByteArrayEqualityComparer.Default.Equals(Root, other.Root);
                   //EqualityComparer<byte[]>.Default.Equals(Root, other.Root);
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
