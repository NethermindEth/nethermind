using System;
using System.Runtime.InteropServices;

namespace Cortex.Containers
{
    public struct ForkVersion : IEquatable<ForkVersion>
    {
        public const int Length = 4;

        private readonly uint _value;

        public ForkVersion(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length, $"{nameof(ForkVersion)} must have exactly {Length} bytes");
            }
            _value = BitConverter.ToUInt32(span);
        }

        public static implicit operator ForkVersion(byte[] bytes) => new ForkVersion(bytes);

        public static implicit operator ForkVersion(Span<byte> span) => new ForkVersion(span);

        public static implicit operator ForkVersion(ReadOnlySpan<byte> span) => new ForkVersion(span);

        public static implicit operator ReadOnlySpan<byte>(ForkVersion hash) => hash.AsSpan();

        public ReadOnlySpan<byte> AsSpan()
        {
            return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1));
        }

        public override bool Equals(object obj)
        {
            return obj is ForkVersion type && Equals(type);
        }

        public bool Equals(ForkVersion other)
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
