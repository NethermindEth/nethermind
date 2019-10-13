using System;
using System.Runtime.InteropServices;

namespace Cortex.Containers
{
    public struct DomainType : IEquatable<DomainType>
    {
        public const int Length = 4;
        public static readonly DomainType BeaconAttester = new DomainType(new byte[] { 0x01, 0x00, 0x00, 0x00 });
        public static readonly DomainType BeaconProposer = new DomainType(new byte[] { 0x00, 0x00, 0x00, 0x00 });
        public static readonly DomainType Deposit = new DomainType(new byte[] { 0x03, 0x00, 0x00, 0x00 });
        public static readonly DomainType DomainTransfer = new DomainType(new byte[] { 0x05, 0x00, 0x00, 0x00 });
        public static readonly DomainType Randao = new DomainType(new byte[] { 0x02, 0x00, 0x00, 0x00 });
        public static readonly DomainType VoluntaryExit = new DomainType(new byte[] { 0x04, 0x00, 0x00, 0x00 });
        private readonly uint _value;

        public DomainType(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length, $"{nameof(DomainType)} must have exactly {Length} bytes");
            }
            _value = BitConverter.ToUInt32(span);
        }

        public static implicit operator DomainType(byte[] bytes) => new DomainType(bytes);

        public static implicit operator DomainType(Span<byte> span) => new DomainType(span);

        public static implicit operator DomainType(ReadOnlySpan<byte> span) => new DomainType(span);

        public static implicit operator ReadOnlySpan<byte>(DomainType hash) => hash.AsSpan();

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
