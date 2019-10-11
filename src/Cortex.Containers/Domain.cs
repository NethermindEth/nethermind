using System;
using System.Runtime.InteropServices;

namespace Cortex.Containers
{
    public struct Domain : IEquatable<Domain>
    {
        public const int Length = 8;

        public static readonly Domain BeaconAttester = new Domain(new byte[] { 0x01, 0x00, 0x00, 0x00 });
        public static readonly Domain BeaconProposer = new Domain(new byte[] { 0x00, 0x00, 0x00, 0x00 });
        public static readonly Domain Deposit = new Domain(new byte[] { 0x03, 0x00, 0x00, 0x00 });
        public static readonly Domain DomainTransfer = new Domain(new byte[] { 0x05, 0x00, 0x00, 0x00 });
        public static readonly Domain Randao = new Domain(new byte[] { 0x02, 0x00, 0x00, 0x00 });
        public static readonly Domain VoluntaryExit = new Domain(new byte[] { 0x04, 0x00, 0x00, 0x00 });
        private readonly ulong _value;

        public Domain(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length, $"{nameof(Domain)} must have exactly {Length} bytes");
            }
            _value = BitConverter.ToUInt32(span);
        }

        public static implicit operator Domain(byte[] bytes) => new Domain(bytes);

        public static implicit operator Domain(Span<byte> span) => new Domain(span);

        public static implicit operator Domain(ReadOnlySpan<byte> span) => new Domain(span);

        public static implicit operator ReadOnlySpan<byte>(Domain hash) => hash.AsSpan();

        public ReadOnlySpan<byte> AsSpan()
        {
            return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1));
        }

        public override bool Equals(object obj)
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
