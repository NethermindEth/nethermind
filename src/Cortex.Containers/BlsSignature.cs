using System;
using System.Runtime.InteropServices;

namespace Cortex.Containers
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BlsSignature
    {
        public const int Length = 96;

        private readonly ulong _a;
        private readonly ulong _b;
        private readonly ulong _c;
        private readonly ulong _d;
        private readonly ulong _e;
        private readonly ulong _f;
        private readonly ulong _g;
        private readonly ulong _h;
        private readonly ulong _i;
        private readonly ulong _j;
        private readonly ulong _k;
        private readonly ulong _l;

        public BlsSignature(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length, $"{nameof(BlsSignature)} must have exactly {Length} bytes");
            }
            var parts = MemoryMarshal.Cast<byte, ulong>(span);
            _a = parts[0];
            _b = parts[1];
            _c = parts[2];
            _d = parts[3];
            _e = parts[4];
            _f = parts[5];
            _g = parts[6];
            _h = parts[7];
            _i = parts[8];
            _j = parts[9];
            _k = parts[10];
            _l = parts[11];
        }

        public static implicit operator BlsSignature(byte[] bytes) => new BlsSignature(bytes);

        public static implicit operator BlsSignature(Span<byte> span) => new BlsSignature(span);

        public static implicit operator BlsSignature(ReadOnlySpan<byte> span) => new BlsSignature(span);

        public static implicit operator ReadOnlySpan<byte>(BlsSignature blsSignature) => blsSignature.AsBytes();
    }
}
