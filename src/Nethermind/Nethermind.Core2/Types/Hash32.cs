using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Core2.Types
{
    public unsafe struct Hash32 : IEquatable<Hash32>, IComparable<Hash32>
    {
        public const int Length = 32;
        
//        private fixed byte Bytes[32];
//
//        public Hash32(ReadOnlySpan<byte> span)
//        {
//            if (span.Length != Length)
//            {
//                throw new ArgumentOutOfRangeException(nameof(span), span.Length, $"{nameof(Hash32)} must have exactly {Length} bytes");
//            }
//
//            fixed (byte* ptr = Bytes)
//            {
//                var output = new Span<byte>(ptr, Length);
//                span.CopyTo(output);
//            }
//        }
//
//        public Span<byte> AsSpan() => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref this, 1));

        /*********************/

        private readonly byte[] _bytes;

        public Hash32(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length, $"{nameof(Hash32)} must have exactly {Length} bytes");
            }

            _bytes = span.ToArray();
        }

        public Span<byte> AsSpan()
        {
            return new Span<byte>(_bytes);
        }

        /*********************/

        public bool Equals(Hash32 other)
        {
            return AsSpan().SequenceEqual(other.AsSpan());
        }

        public static Hash32 Zero { get; } = new Hash32(new byte[32]);

        public static explicit operator Hash32(byte[] bytes) => new Hash32(bytes);

        public static explicit operator Hash32(Span<byte> span) => new Hash32(span);

        public static explicit operator Hash32(ReadOnlySpan<byte> span) => new Hash32(span);

        public static explicit operator ReadOnlySpan<byte>(Hash32 hash) => hash.AsSpan();

        public static bool operator !=(Hash32? left, Hash32? right)
        {
            return !(left == right);
        }

        public static bool operator ==(Hash32? left, Hash32? right)
        {
            if (left is null)
            {
                return right is null;
            }

            return left.Equals(right);
        }

        public int CompareTo(Hash32 other)
        {
            // lexicographic compare
            return AsSpan().SequenceCompareTo(other.AsSpan());
        }

        public override bool Equals(object obj)
        {
            if (obj is null)
            {
                return false;
            }

            return Equals((Hash32) obj);
        }

        public override int GetHashCode()
        {
            return BinaryPrimitives.ReadInt32LittleEndian(AsSpan().Slice(0, 4));
        }

        public override string ToString()
        {
            return AsSpan().ToHexString();
        }

        public Hash32 Xor(Hash32 other)
        {
            return new Hash32(other.AsSpan().Xor(AsSpan()));
        }
    }

//     public unsafe struct Hash32 : IEquatable<Hash32>, IComparable<Hash32>
//    {
//        public const int Length = 32;
//        
//        private fixed byte Bytes[32];
//
//        public Hash32(ReadOnlySpan<byte> span)
//        {
//            if (span.Length != Length)
//            {
//                throw new ArgumentOutOfRangeException(nameof(span), span.Length, $"{nameof(Hash32)} must have exactly {Length} bytes");
//            }
//            
//            fixed (byte* ptr = Bytes)
//            {
//                var output = new Span<byte>(ptr, Length);
//                span.CopyTo(output);
//            }
//        }
//        
//        public Hash32(Span<byte> span)
//        {
//            if (span.Length != Length)
//            {
//                throw new ArgumentOutOfRangeException(nameof(span), span.Length, $"{nameof(Hash32)} must have exactly {Length} bytes");
//            }
//            
//            fixed (byte* ptr = Bytes)
//            {
//                var output = new Span<byte>(ptr, Length);
//                span.CopyTo(output);
//            }
//        }
//        
//        public Span<byte> AsSpan() => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref this, 1));
//    }
}