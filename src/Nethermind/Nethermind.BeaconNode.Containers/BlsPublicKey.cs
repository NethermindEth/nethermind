using System;
using System.Linq;

namespace Nethermind.BeaconNode.Containers
{
    public class BlsPublicKey : IEquatable<BlsPublicKey>
    {
        public const int Length = 48;

        //public static BlsPublicKey Infinity = new BlsPublicKey(new byte[] { 0xC0 }.Concat(Enumerable.Repeat((byte)0x00, Length - 1)).ToArray());

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

        public static BlsPublicKey Clone(BlsPublicKey other)
        {
            return new BlsPublicKey(other.AsSpan());
        }

        public static explicit operator BlsPublicKey(byte[] bytes) => new BlsPublicKey(bytes);

        public static explicit operator BlsPublicKey(Span<byte> span) => new BlsPublicKey(span);

        public static explicit operator BlsPublicKey(ReadOnlySpan<byte> span) => new BlsPublicKey(span);

        public static explicit operator ReadOnlySpan<byte>(BlsPublicKey blsPublicKey) => blsPublicKey.AsSpan();

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
            return !(other is null) &&
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
