using System;
using System.Linq;

namespace Nethermind.Core2.Types
{
    public class Bytes32 : IEquatable<Bytes32>
    {
        public const int Length = 32;

        private readonly byte[] _bytes;

        public Bytes32()
        {
            _bytes = new byte[Length];
        }

        public Bytes32(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length, $"{nameof(Bytes32)} must have exactly {Length} bytes");
            }
            _bytes = span.ToArray();
        }

        public static explicit operator Bytes32(byte[] bytes) => new Bytes32(bytes);

        public static explicit operator Bytes32(Span<byte> span) => new Bytes32(span);

        public static explicit operator Bytes32(ReadOnlySpan<byte> span) => new Bytes32(span);

        public static explicit operator ReadOnlySpan<byte>(Bytes32 hash) => hash.AsSpan();

        public ReadOnlySpan<byte> AsSpan()
        {
            return new ReadOnlySpan<byte>(_bytes);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as Bytes32);
        }

        public bool Equals(Bytes32? other)
        {
            return other != null &&
                _bytes.SequenceEqual(other._bytes);
        }

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var b in _bytes)
            {
                hash.Add(b);
            }
            return hash.ToHashCode();
        }

        public override string ToString()
        {
            return BitConverter.ToString(_bytes).Replace("-", "");
        }
    }
}
