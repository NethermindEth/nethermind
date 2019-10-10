using System;
using System.Runtime.InteropServices;

namespace Cortex.Containers
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct Hash32
    {
        public const int Length = 32;

        private readonly ulong _a;
        private readonly ulong _b;
        private readonly ulong _c;
        private readonly ulong _d;

        public Hash32(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length, $"{nameof(Hash32)} must have exactly {Length} bytes");
            }
            var parts = MemoryMarshal.Cast<byte, ulong>(span);
            _a = parts[0];
            _b = parts[1];
            _c = parts[2];
            _d = parts[3];
        }

        public static implicit operator Hash32(byte[] bytes) => new Hash32(bytes);

        public static implicit operator Hash32(Span<byte> span) => new Hash32(span);

        public static implicit operator Hash32(ReadOnlySpan<byte> span) => new Hash32(span);

        public static implicit operator ReadOnlySpan<byte>(Hash32 hash) => hash.AsBytes();
    }
}
