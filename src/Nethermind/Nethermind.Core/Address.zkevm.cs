// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Nethermind.Core;

public sealed partial class Address
{
    // Two ulongs and a uint: the vector compare has no hardware behind it here.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static partial bool BytesEqual(ref byte a, ref byte b)
        => Unsafe.ReadUnaligned<ulong>(ref a) == Unsafe.ReadUnaligned<ulong>(ref b)
            && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, 8)) == Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8))
            && Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref a, 16)) == Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref b, 16));

    // Always 20 bytes, so skip the length-dispatching FastHash and use the
    // dedicated 20-byte hasher — the dominant Dictionary/FrozenSet probe on zkVM.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal partial int GetHashCodeNonVirtual() => unchecked((int)GetHashCode64());

    // A precompile lives at a low address (top 16 bytes zero), so its trailing number
    // IS the membership key. Returns that number when the top 16 bytes are zero, or -1
    // otherwise — lets IReleaseSpec.IsPrecompile swap a FrozenSet hash+probe for a bitmask.
    public int PrecompileIndexOrNegative()
    {
        ref byte b = ref Unsafe.AsRef(in FirstByte);
        if ((Unsafe.ReadUnaligned<ulong>(ref b) | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8))) != 0)
        {
            return -1;
        }

        // bytes 16..19, big-endian
        uint tail = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref b, 16));
        return (int)BinaryPrimitives.ReverseEndianness(tail);
    }
}
