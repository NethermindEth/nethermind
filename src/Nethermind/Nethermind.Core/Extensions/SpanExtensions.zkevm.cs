// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
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

        // No field initializers: the guest must not pay a class-constructor check on each hash call.
        internal static uint InstanceRandom;
        private static ulong[]? AddressSeeds;
        private static ulong[]? WordSeeds;
        private const int WordWidth = 32;

        /// <inheritdoc />
        public static partial void SeedHashes(in Int256.UInt256 seed)
        {
            WordSeeds = [seed.u0, seed.u1, seed.u2, seed.u3];
            AddressSeeds = [DeriveAddressSeed(seed.u0), DeriveAddressSeed(seed.u1),
                DeriveAddressSeed(seed.u2), DeriveAddressSeed(seed.u3)];
            ulong folded = MultiplyFold(seed.u0 ^ AesHashSeed0, seed.u1 ^ AesHashSeed1)
                ^ MultiplyFold(seed.u2 ^ AesHash32Seed0, seed.u3 ^ AesHash32Seed1);
            InstanceRandom = (uint)(folded ^ (folded >> 32));
        }

        // Width-specific nonlinear derivation keeps addresses separate from padded 32-byte keys.
        private static ulong DeriveAddressSeed(ulong word)
        {
            word += 0x9E3779B97F4A7C15UL + Address.Size;
            word = (word ^ (word >> 30)) * 0xBF58476D1CE4E5B9UL;
            word = (word ^ (word >> 27)) * 0x94D049BB133111EBUL;
            return word ^ (word >> 31);
        }

        [Conditional("DEBUG")]
        private static void AssertSeeded() =>
            Debug.Assert(WordSeeds is not null, $"{nameof(SeedHashes)} must run before the guest hashes a key.");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int FastHashFallback(ReadOnlySpan<byte> input)
        {
            AssertSeeded();
            ulong hash = input.Length switch
            {
                WordWidth => Mix32(ref MemoryMarshal.GetReference(input)),
                Address.Size => MixAddress(ref MemoryMarshal.GetReference(input)),
                _ => MixBytes(input)
            };
            return (int)(hash ^ (hash >> 32));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MixAddress(ref byte b)
        {
            AssertSeeded();
            ref ulong seeds = ref MemoryMarshal.GetArrayDataReference(AddressSeeds!);
            return MixWords(
                Unsafe.ReadUnaligned<ulong>(ref b),
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8)),
                Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref b, 16)), 0, ref seeds);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static long FastHash64For32BytesFallback(ref byte start)
            => (long)Mix32(ref start);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Mix32(ref byte b)
        {
            AssertSeeded();
            ref ulong seeds = ref MemoryMarshal.GetArrayDataReference(WordSeeds!);
            return MixWords(
                Unsafe.ReadUnaligned<ulong>(ref b),
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8)),
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 16)),
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 24)), ref seeds);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MixWords(ulong u0, ulong u1, ulong u2, ulong u3, ref ulong seeds)
        {
            // Mix the secret into full-width limbs before any information is lost to folding.
            ulong a = MultiplyFold(u0 ^ seeds, u1 ^ Unsafe.Add(ref seeds, 1));
            ulong b = MultiplyFold(u2 ^ Unsafe.Add(ref seeds, 2), u3 ^ Unsafe.Add(ref seeds, 3));
            return (ulong)MumFold(a, b);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MultiplyFold(ulong a, ulong b)
        {
            ulong high = Math.BigMul(a, b, out ulong low);
            return low ^ high;
        }

        private static ulong MixBytes(ReadOnlySpan<byte> input)
        {
            ref ulong seeds = ref MemoryMarshal.GetArrayDataReference(WordSeeds!);
            ulong hash = MixWords((uint)input.Length, ShortInputDomain, 0, 0, ref seeds);
            while (input.Length >= 16)
            {
                ref byte start = ref MemoryMarshal.GetReference(input);
                ulong block = MultiplyFold(Unsafe.ReadUnaligned<ulong>(ref start) ^ seeds,
                    Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, 8)) ^ Unsafe.Add(ref seeds, 1));
                hash = (ulong)MumFold(hash ^ block, Unsafe.Add(ref seeds, 2));
                input = input[16..];
            }

            // The length participates before the blocks, so zero-padding the tail is unambiguous.
            ulong low = 0, high = 0;
            for (int i = 0; i < input.Length; i++)
            {
                if (i < 8) low |= (ulong)input[i] << (i * 8);
                else high |= (ulong)input[i] << ((i - 8) * 8);
            }
            ulong tail = MultiplyFold(low ^ seeds, high ^ Unsafe.Add(ref seeds, 1));
            return (ulong)MumFold(hash ^ tail, Unsafe.Add(ref seeds, 3));
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
