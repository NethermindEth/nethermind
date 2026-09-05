// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Core.Extensions;

public static partial class EvmWordExtensions
{
    extension(EvmWord word)
    {
        /// <summary>
        /// Reverses the byte order of a 32-byte word (big-endian &lt;-&gt; little-endian).
        /// AVX-512 VBMI: single PermuteVar32x8. AVX2: Permute4x64 lane-swap + per-lane PSHUFB.
        /// AdvSimd (ARM64): 2x REV64 + 2x EXT #8 half-rotate.
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
            if (AdvSimd.Arm64.IsSupported)
            {
                Vector128<ulong> reversedUpper = AdvSimd.ReverseElement8(word.GetUpper().AsUInt64());
                Vector128<ulong> reversedLower = AdvSimd.ReverseElement8(word.GetLower().AsUInt64());
                Vector128<ulong> lower = AdvSimd.ExtractVector128(reversedUpper, reversedUpper, 1);
                Vector128<ulong> upper = AdvSimd.ExtractVector128(reversedLower, reversedLower, 1);
                return Vector256.Create(lower, upper).AsByte();
            }

            return ByteSwapScalar(word);
        }
    }

    /// <summary>Byte-reverses a 32-byte word without SIMD.</summary>
    /// <param name="word">The word to reverse.</param>
    /// <returns><paramref name="word"/> with its bytes in the opposite order.</returns>
    /// <remarks>Split per target. Both arms reverse the same four lanes and swap their order; they differ
    /// only in how they get at them. A host reaches this at all only without AVX2 or AdvSimd, so it keeps
    /// the vector spelling; the guest has no vectors, where <c>GetElement</c> and <c>Vector256.Create</c>
    /// become a stack round-trip per lane around a byte swap that is already only a few instructions.
    /// See <c>EvmWordExtensions.std.cs</c> and <c>.zkevm.cs</c>.</remarks>
    private static partial EvmWord ByteSwapScalar(EvmWord word);

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
