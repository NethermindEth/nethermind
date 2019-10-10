using System;
using System.Collections.Generic;

namespace Cortex.Containers
{
    public class Hash32 : IEquatable<Hash32>
    {
        public const int Length = 32;

        private readonly byte[] _bytes;

        public Hash32()
        {
            _bytes = new byte[Length];
        }

        public Hash32(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length, $"{nameof(Hash32)} must have exactly {Length} bytes");
            }
            _bytes = span.ToArray();
        }

        public static implicit operator Hash32(byte[] bytes) => new Hash32(bytes);

        public static implicit operator Hash32(Span<byte> span) => new Hash32(span);

        public static implicit operator Hash32(ReadOnlySpan<byte> span) => new Hash32(span);

        public static implicit operator ReadOnlySpan<byte>(Hash32 hash) => hash.AsSpan();

        public ReadOnlySpan<byte> AsSpan()
        {
            return new ReadOnlySpan<byte>(_bytes);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as Hash32);
        }

        public bool Equals(Hash32 other)
        {
            return other != null &&
                   EqualityComparer<byte[]>.Default.Equals(_bytes, other._bytes);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(_bytes);
        }

        public override string ToString()
        {
            return BitConverter.ToString(_bytes).Replace("-", "");
        }
    }
}
