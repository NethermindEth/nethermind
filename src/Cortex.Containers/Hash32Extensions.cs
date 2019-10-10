using System;
using System.Runtime.InteropServices;

namespace Cortex.Containers
{
    public static class Hash32Extensions
    {
        public static ReadOnlySpan<byte> AsBytes(this Hash32 hash)
        {
            return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref hash, 1));
        }
    }
}
