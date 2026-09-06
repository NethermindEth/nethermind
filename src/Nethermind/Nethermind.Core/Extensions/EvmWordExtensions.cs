// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Core.Extensions;

public static class EvmWordExtensions
{
    extension(EvmWord word)
    {
        /// <summary>
        /// Reverses the byte order of a 32-byte word (big-endian &lt;-&gt; little-endian).
        /// AVX-512 VBMI: single PermuteVar32x8. AVX2: Permute4x64 lane-swap + per-lane PSHUFB.
        /// AdvSimd (ARM64): one TBL per 128-bit half. REV64 + EXT reverses a half too, but as two
        /// dependent operations; the index vector TBL needs is independent of the word.
        /// Scalar fallback: 4x ReverseEndianness with ulong reorder.
        /// </summary>
        [SkipLocalsInit]
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
            if (AdvSimd.Arm64.IsSupported)
            {
                Vector128<byte> reverse = ReverseBytes128Mask;
                return Vector256.Create(
                    AdvSimd.Arm64.VectorTableLookup(word.GetUpper(), reverse),
                    AdvSimd.Arm64.VectorTableLookup(word.GetLower(), reverse));
            }

            Unsafe.SkipInit(out EvmWord result);
            ref ulong source = ref Unsafe.As<EvmWord, ulong>(ref word);
            ref ulong destination = ref Unsafe.As<EvmWord, ulong>(ref result);
            destination = Bytes.Bswap64(Unsafe.Add(ref source, 3));
            Unsafe.Add(ref destination, 1) = Bytes.Bswap64(Unsafe.Add(ref source, 2));
            Unsafe.Add(ref destination, 2) = Bytes.Bswap64(Unsafe.Add(ref source, 1));
            Unsafe.Add(ref destination, 3) = Bytes.Bswap64(source);
            return result;
        }
    }

    // TBL index vector that byte-reverses one 128-bit half.
    private static Vector128<byte> ReverseBytes128Mask
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector128.Create((byte)15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0);
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
