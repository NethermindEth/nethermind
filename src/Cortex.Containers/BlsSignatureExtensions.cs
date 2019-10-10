using System;
using System.Runtime.InteropServices;

namespace Cortex.Containers
{
    public static class BlsSignatureExtensions
    {
        public static ReadOnlySpan<byte> AsBytes(this BlsSignature blsSignature)
        {
            return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref blsSignature, 1));
        }
    }
}
