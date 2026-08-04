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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FastHashFallback(ReadOnlySpan<byte> input)
            => FastHashCrc(ref MemoryMarshal.GetReference(input), input.Length, ComputeSeed(input.Length));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long FastHash64For32BytesFallback(ref byte start)
            => FastHash64For32BytesCrc(ref start, ComputeSeed(32));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long FastHash64For20BytesFallback(ref byte start)
            => FastHash64For20BytesCrc(ref start, ComputeSeed(20));

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
