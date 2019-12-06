using System;
using System.Runtime.InteropServices;

namespace Cortex.Containers
{
    public struct DomainType : IEquatable<DomainType>
    {
        public const int Length = 4;
        private readonly uint _value;

        public DomainType(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length, $"{nameof(DomainType)} must have exactly {Length} bytes");
            }
            _value = BitConverter.ToUInt32(span);
        }

        public static explicit operator DomainType(byte[] bytes) => new DomainType(bytes);

        public static explicit operator DomainType(Span<byte> span) => new DomainType(span);

        public static explicit operator DomainType(ReadOnlySpan<byte> span) => new DomainType(span);

        public static explicit operator ReadOnlySpan<byte>(DomainType hash) => hash.AsSpan();

        public static bool operator !=(DomainType left, DomainType right)
        {
            return !(left == right);
        }

        public static bool operator ==(DomainType left, DomainType right)
        {
            return left.Equals(right);
        }

        public ReadOnlySpan<byte> AsSpan()
        {
            return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1));
        }

        public override bool Equals(object obj)
        {
            return obj is DomainType type && Equals(type);
        }

        public bool Equals(DomainType other)
        {
            return _value == other._value;
        }

        public override int GetHashCode()
        {
            return _value.GetHashCode();
        }

        public override string ToString()
        {
            return BitConverter.ToString(AsSpan().ToArray()).Replace("-", "");
        }
    }
}
