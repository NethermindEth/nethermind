// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Nethermind.Core;

public sealed partial class Address
{
    // RISC-V has no SIMD; a Vector128 compare lowers to a slow software helper.
    // Compare the 20 bytes as two ulongs plus a uint instead.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial bool Equals20Bytes(ref byte a, ref byte b) =>
        Unsafe.ReadUnaligned<ulong>(ref a)
                == Unsafe.ReadUnaligned<ulong>(ref b)
        && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, 8))
                == Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8))
        && Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref a, 16))
                == Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref b, 16));

    // Address is always 20 bytes — skip the generic length-dispatching FastHash and use the
    // dedicated 20-byte hasher. Dominant Dictionary/FrozenSet probe cost on the ZisK target.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override partial int GetHashCode() => unchecked((int)GetHashCode64());

    /// <summary>
    /// Returns the precompile address number when the top 16 bytes are zero, or -1 otherwise.
    /// Lets <see cref="Specs.IReleaseSpecExtensions"/>.IsPrecompile replace a
    /// <c>FrozenSet</c> hash+probe with a bitmask.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PrecompileIndexOrNegative()
    {
        ref byte b = ref Unsafe.AsRef(in FirstByte);
        if ((Unsafe.ReadUnaligned<ulong>(ref b)
             | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8))) != 0)
        {
            return -1;
        }

        // bytes 16..19, big-endian
        uint tail = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref b, 16));
        return (int)BinaryPrimitives.ReverseEndianness(tail);
    }
}
