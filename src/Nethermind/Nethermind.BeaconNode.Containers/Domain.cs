using System;
using System.Runtime.InteropServices;

namespace Nethermind.BeaconNode.Containers
{
    public struct Domain : IEquatable<Domain>
    {
        public const int Length = 8;

        private readonly ulong _value;

        public Domain(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length, $"{nameof(Domain)} must have exactly {Length} bytes");
            }
            _value = BitConverter.ToUInt32(span);
        }

        public static explicit operator Domain(byte[] bytes) => new Domain(bytes);

        public static explicit operator Domain(Span<byte> span) => new Domain(span);

        public static explicit operator Domain(ReadOnlySpan<byte> span) => new Domain(span);

        public static explicit operator ReadOnlySpan<byte>(Domain item) => item.AsSpan();

        public static bool operator !=(Domain left, Domain right)
        {
            return !(left == right);
        }

        public static bool operator ==(Domain left, Domain right)
        {
            return left.Equals(right);
        }

        public ReadOnlySpan<byte> AsSpan()
        {
            return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1));
        }

        public override bool Equals(object? obj)
        {
            return obj is Domain type && Equals(type);
        }

        public bool Equals(Domain other)
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
