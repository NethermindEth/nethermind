using System.Collections;

namespace Lantern.Discv5.WireProtocol.Utility;

public static class ByteArrayEqualityComparer
{
    public static readonly IEqualityComparer<byte[]> Instance = new ByteArrayEqualityComparerImplementation();

    private sealed class ByteArrayEqualityComparerImplementation : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y)
        {
            return StructuralComparisons.StructuralEqualityComparer.Equals(x, y);
        }

        public int GetHashCode(byte[] obj)
        {
            return StructuralComparisons.StructuralEqualityComparer.GetHashCode(obj);
        }
    }
}