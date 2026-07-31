// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Extensions
{
    public static partial class SpanExtensions
    {
        // Ensure that hashes are different for every run of the node and every node, so if there are any hash collisions
        // on one node, they will not be the same on another node or across a restart and cannot degrade the network as a whole.
        public static readonly uint InstanceRandom =
            (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);

        private static readonly ulong AesHashSeed0 = CreateAesHashSeed();
        private static readonly ulong AesHashSeed1 = CreateAesHashSeed();
        private static readonly ulong AesHash20Seed0 = CreateAesHashSeed();
        private static readonly ulong AesHash20Seed1 = CreateAesHashSeed();
        private static readonly ulong AesHash32Seed0 = CreateAesHashSeed();
        private static readonly ulong AesHash32Seed1 = CreateAesHashSeed();
        private static readonly ulong AesHashFinalSeed0 = CreateAesHashSeed();
        private static readonly ulong AesHashFinalSeed1 = CreateAesHashSeed();
        private static readonly long XxHashSeed = unchecked((long)CreateAesHashSeed());
        private static readonly long FastHash20XxSeed = unchecked((long)CreateAesHashSeed());
        private static readonly long FastHash32XxSeed = unchecked((long)CreateAesHashSeed());

        [SkipLocalsInit]
        private static ulong CreateAesHashSeed()
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FastHashFallback(ReadOnlySpan<byte> input)
            => FastHashXxHash3(input, XxHashSeed);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long FastHash64For32BytesFallback(ref byte start)
            => FastHash64XxHash3(ref start, 32, FastHash32XxSeed);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long FastHash64For20BytesFallback(ref byte start)
            => FastHash64XxHash3(ref start, 20, FastHash20XxSeed);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int FastHashXxHash3(ReadOnlySpan<byte> input, long seed)
        {
            ulong hash = XxHash3.HashToUInt64(input, seed);
            return (int)(hash ^ (hash >> 32));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static long FastHash64XxHash3(ref byte start, int length, long seed)
            => unchecked((long)XxHash3.HashToUInt64(MemoryMarshal.CreateReadOnlySpan(ref start, length), seed));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Crc32C(uint crc, byte data) => BitOperations.Crc32C(crc, data);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Crc32C(uint crc, ushort data) => BitOperations.Crc32C(crc, data);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Crc32C(uint crc, uint data) => BitOperations.Crc32C(crc, data);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Crc32C(uint crc, ulong data) => BitOperations.Crc32C(crc, data);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint CrcLane(uint crc, ulong data) => BitOperations.Crc32C(crc, data) * 0xCC9E2D51u;
    }
}
