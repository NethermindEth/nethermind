// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Extensions
{
    public static partial class SpanExtensions
    {
        private const ulong AesHashSeed0 = 0x6A09E667F3BCC909UL;
        private const ulong AesHashSeed1 = 0xBB67AE8584CAA73BUL;
        private const ulong AesHash20Seed0 = 0x510E527FADE682D1UL;
        private const ulong AesHash20Seed1 = 0x9B05688C2B3E6C1FUL;
        private const ulong AesHashPairSeed0 = 0xCBBB9D5DC1059ED8UL;
        private const ulong AesHashPairSeed1 = 0x629A292A367CD507UL;
        private const ulong AesHash32Seed0 = 0x1F83D9ABFB41BD6BUL;
        private const ulong AesHash32Seed1 = 0x5BE0CD19137E2179UL;
        private const ulong AesHashFinalSeed0 = 0x3C6EF372FE94F82BUL;
        private const ulong AesHashFinalSeed1 = 0xA54FF53A5F1D36F1UL;

        // Guest execution requires stable hashes across runs.
        public static readonly uint InstanceRandom = 2098026241U;

        // Distinct odd multipliers so a lane's contribution depends on its position: a plain XOR fold
        // would collide for inputs that differ only by swapping two lanes.
        private const ulong Lane0 = 0x9E3779B97F4A7C15UL;
        private const ulong Lane1 = 0xC2B2AE3D27D4EB4FUL;
        private const ulong Lane2 = 0x165667B19E3779F9UL;
        private const ulong Lane3 = 0x85EBCA77C2B2AE63UL;
        private const int WordWidth = 32;

        /// <summary>Seeds a lane multiplier for one key width.</summary>
        /// <remarks>
        /// The seed has to reach the lane products rather than only the accumulated result. Applied
        /// after the lanes are combined it cancels in the difference between two keys, so any
        /// colliding pair found for one seed holds for every seed, and the mixer is no harder to
        /// attack than an unseeded one. Folding it into the multipliers keeps the dependence
        /// key-dependent at no extra arithmetic -- the same multiplies, against seeded operands.
        /// Shifting left of the low bit keeps each multiplier odd, so it stays a bijection.
        /// Seeding by <see cref="ComputeSeed"/> of the width also separates widths, which the shared
        /// lane constants did not: a 20-byte key and its zero-padded 32-byte form previously mixed
        /// to the same value, because the tail read of the shorter key is the zero-extension of the
        /// longer one's and the unused lane contributes nothing.
        /// <para>
        /// This placement does not by itself make the mixer hard to collide.
        /// <see cref="InstanceRandom"/> is a fixed literal in the guest, so the seeded multipliers
        /// are public constants and remain odd and invertible: the same closed-form derivation
        /// applies to them. What it buys is the width separation above, which fixes a present bug,
        /// and a mixer that a per-run seed would actually harden instead of cancelling.
        /// </para>
        /// </remarks>
        private static ulong SeededLane(ulong lane, int width) =>
            lane ^ ((ulong)ComputeSeed(width) << 1);

        private static readonly ulong AddrLane0 = SeededLane(Lane0, Address.Size);
        private static readonly ulong AddrLane1 = SeededLane(Lane1, Address.Size);
        private static readonly ulong AddrLane2 = SeededLane(Lane2, Address.Size);

        private static readonly ulong WordLane0 = SeededLane(Lane0, WordWidth);
        private static readonly ulong WordLane1 = SeededLane(Lane1, WordWidth);
        private static readonly ulong WordLane2 = SeededLane(Lane2, WordWidth);
        private static readonly ulong WordLane3 = SeededLane(Lane3, WordWidth);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FastHashFallback(ReadOnlySpan<byte> input)
        {
            // 32 bytes is the dominant key width in the guest (trie node hashes, storage cells), and
            // the four-lane CRC-style walk is one of its hottest leaves. Every byte still feeds the
            // result -- folding to the leading word would collide across big-endian UInt256 values,
            // which share leading zeros -- but at four multiplies instead of a lane-at-a-time walk.
            if (input.Length == WordWidth)
            {
                return (int)(uint)Mix32(ref MemoryMarshal.GetReference(input));
            }

            // Addresses are the other dominant key width.
            if (input.Length == Address.Size)
            {
                return (int)(uint)MixAddress(ref MemoryMarshal.GetReference(input));
            }

            return FastHashCrc(ref MemoryMarshal.GetReference(input), input.Length, ComputeSeed(input.Length));
        }

        /// <summary>Mixes the twenty bytes of an address into a well-distributed 64-bit value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MixAddress(ref byte b) => Finish(
            Unsafe.ReadUnaligned<ulong>(ref b) * AddrLane0 ^
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8)) * AddrLane1 ^
            Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref b, 16)) * AddrLane2,
            AddrLane0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static long FastHash64For32BytesFallback(ref byte start)
            => (long)Mix32(ref start);

        /// <summary>Mixes thirty-two bytes into a well-distributed 64-bit value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Mix32(ref byte b) => Finish(
            Unsafe.ReadUnaligned<ulong>(ref b) * WordLane0 ^
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8)) * WordLane1 ^
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 16)) * WordLane2 ^
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 24)) * WordLane3,
            WordLane0);

        /// <summary>Finishes a lane combination into a well-distributed 64-bit value.</summary>
        /// <remarks>
        /// One multiply between two folds, both by 32, and both folds are load-bearing. The lane
        /// multiplies carry upward only, so the pre-fold is what lets a key whose entropy sits in the
        /// high half of a lane -- a zero-padded value at offset 4, 12, 20 or 28 -- reach the low output
        /// bits at all; the post-fold brings the multiply's concentrated high half back down. Dropping
        /// either collapses a 14-bit bucket window to 2048-2679 distinct values on 4096 samples, the
        /// same failure the AES path documents at <see cref="FastHash64For20Bytes"/>. A second multiply
        /// buys nothing further, and <c>GuestMixerTests</c> covers every aligned offset.
        /// </remarks>
        /// <param name="mixed">The combined lane products.</param>
        /// <param name="domain">
        /// A seeded lane multiplier for the same key width. Avalanche only, since a value applied
        /// here cannot make a colliding pair diverge -- see <see cref="SeededLane"/> — but it keeps
        /// an all-zero key off a fixed point.
        /// </param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Finish(ulong mixed, ulong domain)
        {
            mixed ^= mixed >> 32;
            mixed ^= domain;
            mixed *= Lane0;
            return mixed ^ (mixed >> 32);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static long FastHash64For20BytesFallback(ref byte start)
            => (long)MixAddress(ref start);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Crc32C(uint crc, byte data) => ZkEvmBitOperations.Crc32C(crc, data);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Crc32C(uint crc, ushort data) => ZkEvmBitOperations.Crc32C(crc, data);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Crc32C(uint crc, uint data) => ZkEvmBitOperations.Crc32C(crc, data);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Crc32C(uint crc, ulong data) => ZkEvmBitOperations.Crc32C(crc, data);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint CrcLane(uint crc, ulong data) => ZkEvmBitOperations.Crc32C(crc, data);
    }
}
