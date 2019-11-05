using System;
using System.Linq;

namespace Cortex.Containers
{
    public class Hash32 : IEquatable<Hash32>
    {
        public const int Length = 32;

        private readonly byte[] _bytes;

        public Hash32(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length, $"{nameof(Hash32)} must have exactly {Length} bytes");
            }
            _bytes = span.ToArray();
        }

        private Hash32()
        {
            _bytes = new byte[Length];
        }

        public static Hash32 Zero { get; } = new Hash32();

        /// <summary>
        /// Creates a deep copy of the object.
        /// </summary>
        public static Hash32 Clone(Hash32 other)
        {
            return new Hash32(other.AsSpan());
        }

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

        public ReadOnlySpan<byte> AsSpan()
        {
            return new ReadOnlySpan<byte>(_bytes);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as Hash32);
        }

        public bool Equals(Hash32? other)
        {
            return !(other is null) &&
                _bytes.SequenceEqual(other._bytes);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(_bytes);
        }

        public override string ToString()
        {
            return BitConverter.ToString(_bytes).Replace("-", "");
        }

        public Hash32 Xor(Hash32 other)
        {
            var xorBytes = _bytes.Zip(other._bytes, (a, b) => (byte)(a ^ b)).ToArray();
            return new Hash32(xorBytes);
        }
    }
}
