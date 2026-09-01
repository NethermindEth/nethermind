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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FastHashFallback(ReadOnlySpan<byte> input)
        {
            // 32 bytes is the dominant key width in the guest (trie node hashes, storage cells), and
            // the four-lane CRC-style walk is one of its hottest leaves. Every byte still feeds the
            // result -- folding to the leading word would collide across big-endian UInt256 values,
            // which share leading zeros -- but at four multiplies instead of a lane-at-a-time walk.
            if (input.Length == 32)
            {
                return (int)(uint)Mix32(ref MemoryMarshal.GetReference(input));
            }

            // Addresses are the other dominant key width.
            if (input.Length == 20)
            {
                return (int)(uint)MixAddress(ref MemoryMarshal.GetReference(input));
            }

            return FastHashCrc(ref MemoryMarshal.GetReference(input), input.Length, ComputeSeed(input.Length));
        }

        /// <summary>Mixes the twenty bytes of an address into a well-distributed 64-bit value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MixAddress(ref byte b)
        {
            ulong mixed =
                Unsafe.ReadUnaligned<ulong>(ref b) * Lane0 ^
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8)) * Lane1 ^
                Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref b, 16)) * Lane2;

            mixed ^= InstanceRandom;
            mixed *= Lane0;
            return mixed ^ (mixed >> 29);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long FastHash64For32BytesFallback(ref byte start)
            => (long)Mix32(ref start);

        /// <summary>Mixes thirty-two bytes into a well-distributed 64-bit value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Mix32(ref byte b)
        {
            ulong mixed =
                Unsafe.ReadUnaligned<ulong>(ref b) * Lane0 ^
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8)) * Lane1 ^
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 16)) * Lane2 ^
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 24)) * Lane3;

            mixed ^= InstanceRandom;
            mixed *= Lane0;
            return mixed ^ (mixed >> 29);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long FastHash64For20BytesFallback(ref byte start)
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
