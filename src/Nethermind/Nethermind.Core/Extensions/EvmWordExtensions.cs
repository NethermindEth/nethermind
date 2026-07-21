// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Core.Extensions;

public static class EvmWordExtensions
{
    extension(EvmWord word)
    {
        /// <summary>
        /// Reverses the byte order of a 32-byte word (big-endian &lt;-&gt; little-endian).
        /// AVX-512 VBMI: single PermuteVar32x8. AVX2: Permute4x64 lane-swap + per-lane PSHUFB.
        /// Scalar fallback: 4x ReverseEndianness with ulong reorder.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EvmWord ByteSwap()
        {
            if (Avx512Vbmi.VL.IsSupported)
            {
                return Avx512Vbmi.VL.PermuteVar32x8(word, ByteSwap256Mask);
            }
            if (Avx2.IsSupported)
            {
                Vector256<ulong> permute = Avx2.Permute4x64(word.AsUInt64(), 0b_01_00_11_10);
                return Avx2.Shuffle(permute.AsByte(), ByteSwap256Mask);
            }

            Vector256<ulong> u = word.AsUInt64();
            ulong out0 = BinaryPrimitives.ReverseEndianness(u.GetElement(3));
            ulong out1 = BinaryPrimitives.ReverseEndianness(u.GetElement(2));
            ulong out2 = BinaryPrimitives.ReverseEndianness(u.GetElement(1));
            ulong out3 = BinaryPrimitives.ReverseEndianness(u.GetElement(0));
            return Vector256.Create(out0, out1, out2, out3).AsByte();
        }
    }

    // PSHUFB / PermuteVar32x8 mask that byte-reverses a 256-bit word.
    // Property form so the JIT folds it to a PC-relative rodata load at every call site.
    internal static EvmWord ByteSwap256Mask
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector256.Create(
            0x18191a1b1c1d1e1ful,
            0x1011121314151617ul,
            0x08090a0b0c0d0e0ful,
            0x0001020304050607ul).AsByte();
    }
}
