using System;
using System.Collections.Generic;

namespace Nethermind.BeaconNode.Containers
{
    public class ByteArrayEqualityComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayEqualityComparer Default = new ByteArrayEqualityComparer();

        public bool Equals(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
        {
            return x.SequenceEqual(y);
        }

        public int GetHashCode(ReadOnlySpan<byte> obj)
        {
            unchecked
            {
                var hash = 13;
                foreach (var b in obj)
                {
                    hash = hash * 7 + b;
                }
                return hash;
            }
        }

        bool IEqualityComparer<byte[]>.Equals(byte[] x, byte[] y)
        {
            return Equals(x, y);
        }

        int IEqualityComparer<byte[]>.GetHashCode(byte[] obj)
        {
            return GetHashCode(obj);
        }
    }
}
