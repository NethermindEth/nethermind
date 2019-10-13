using System;
using System.Linq;

namespace Cortex.Containers
{
    public class BlsPublicKey : IEquatable<BlsPublicKey>
    {
        public const int Length = 48;

        private readonly byte[] _bytes;

        public BlsPublicKey()
        {
            _bytes = new byte[Length];
        }

        public BlsPublicKey(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length, $"{nameof(BlsPublicKey)} must have exactly {Length} bytes");
            }
            _bytes = span.ToArray();
        }

        public static implicit operator BlsPublicKey(byte[] bytes) => new BlsPublicKey(bytes);

        public static implicit operator BlsPublicKey(Span<byte> span) => new BlsPublicKey(span);

        public static implicit operator BlsPublicKey(ReadOnlySpan<byte> span) => new BlsPublicKey(span);

        public static implicit operator ReadOnlySpan<byte>(BlsPublicKey blsPublicKey) => blsPublicKey.AsSpan();

        public ReadOnlySpan<byte> AsSpan()
        {
            return new ReadOnlySpan<byte>(_bytes);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as BlsPublicKey);
        }

        public bool Equals(BlsPublicKey? other)
        {
            return other != null &&
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
    }
}
