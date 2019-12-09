using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Nethermind.Core2.Types
{
    public unsafe struct Hash32 : IEquatable<Hash32>, IComparable<Hash32>
    {
        public const int Length = 32;
        
        public const int SszLength = 32;
        
        public Hash32(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length, $"{nameof(Hash32)} must have exactly {Length} bytes");
            }

            Bytes = new byte[32];
            fixed (byte* ptr = Bytes)
            {
                var output = new Span<byte>(ptr, Length);
                span.CopyTo(output);
            }
            
            // simpler but keeping above for easier testing of fixed
            // _bytes = span.ToArray();
        } 

        // tests are not passing when using fixed
        //        private fixed byte Bytes[32];
        //        public Span<byte> AsSpan() => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref this, 1));
        
        public readonly byte[] Bytes;
        
        public Span<byte> AsSpan()
        {
            return new Span<byte>(Bytes);
        }
        
        public Span<byte> AsSpan(int startIndex, int length)
        {
            return Bytes.AsSpan(startIndex, length);
        }

        public bool Equals(Hash32 other)
        {
            return AsSpan().SequenceEqual(other.AsSpan());
        }

        public static Hash32 Zero { get; } = new Hash32(new byte[32]);

        public static bool operator !=(Hash32 left, Hash32 right)
        {
            return !(left == right);
        }

        public static bool operator ==(Hash32 left, Hash32 right)
        {
            return left.Equals(right);
        }

        public int CompareTo(Hash32 other)
        {
            // lexicographic compare
            return AsSpan().SequenceCompareTo(other.AsSpan());
        }

        public override bool Equals(object? obj)
        {
            return !(obj is null) && Equals((Hash32) obj);
        }

        public override int GetHashCode()
        {
            return BinaryPrimitives.ReadInt32LittleEndian(AsSpan().Slice(0, 4));
        }

        public override string ToString()
        {
            return AsSpan().ToHexString(true);
        }

        public Hash32 Xor(Hash32 other)
        {
            return new Hash32(other.AsSpan().Xor(AsSpan()));
        }
    }
}