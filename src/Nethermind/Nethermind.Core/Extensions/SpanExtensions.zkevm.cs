// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Nethermind.Core.Extensions
{
    public static partial class SpanExtensions
    {
        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static partial int CombineHash(uint hash, ulong value)
        {
            ReadOnlySpan<ulong> words = [hash, value];
            return MemoryMarshal.AsBytes(words).FastHash();
        }

        private static ulong AesHashSeed0;
        private static ulong AesHashSeed1;
        private static ulong AesHash20Seed0;
        private static ulong AesHash20Seed1;
        private static ulong AesHashPairSeed0;
        private static ulong AesHashPairSeed1;
        private static ulong AesHash32Seed0;
        private static ulong AesHash32Seed1;
        private static ulong AesHashFinalSeed0;
        private static ulong AesHashFinalSeed1;

        private static Vector128<byte> AesHashSeed => Vector128.Create(AesHashSeed0, AesHashSeed1).AsByte();
        private static Vector128<byte> AesHash20Seed => Vector128.Create(AesHash20Seed0, AesHash20Seed1).AsByte();
        private static Vector128<byte> AesHashPairSeed => Vector128.Create(AesHashPairSeed0, AesHashPairSeed1).AsByte();
        private static Vector128<byte> AesHash32Seed => Vector128.Create(AesHash32Seed0, AesHash32Seed1).AsByte();
        private static Vector128<byte> AesHashFinalSeed => Vector128.Create(AesHashFinalSeed0, AesHashFinalSeed1).AsByte();

        // No field initializers: the guest must not pay a class-constructor check on each hash call.
        internal static Int256.UInt256 InstanceRandom;
        // Independent of the lane keys: NH's bound assumes the finalizer key is not one of them.
        private static ulong HashFinalizerKey;
        private static ulong[]? AddressSeeds;
        private static ulong[]? ShortHashSeeds;

        /// <inheritdoc />
        public static partial void SeedHashes(in Int256.UInt256 seed)
        {
            InstanceRandom = seed;
            // Installed before anything mixes, which CreateShortHashSeeds below does.
            HashFinalizerKey = MultiplyFold(seed.u0 ^ FinalizerDomain, seed.u3 ^ ~FinalizerDomain) | 1UL;
            ShortHashSeeds = CreateShortHashSeeds(in InstanceRandom);
            AddressSeeds = [DeriveAddressSeed(seed.u0), DeriveAddressSeed(seed.u1),
                DeriveAddressSeed(seed.u2), DeriveAddressSeed(seed.u3)];
            AesHashSeed0 = seed.u0 ^ 0x6A09E667F3BCC909UL;
            AesHashSeed1 = seed.u1 ^ 0xBB67AE8584CAA73BUL;
            AesHash20Seed0 = seed.u0 ^ 0x510E527FADE682D1UL;
            AesHash20Seed1 = seed.u1 ^ 0x9B05688C2B3E6C1FUL;
            AesHashPairSeed0 = seed.u0 ^ 0xCBBB9D5DC1059ED8UL;
            AesHashPairSeed1 = seed.u1 ^ 0x629A292A367CD507UL;
            AesHash32Seed0 = seed.u0 ^ 0x1F83D9ABFB41BD6BUL;
            AesHash32Seed1 = seed.u1 ^ 0x5BE0CD19137E2179UL;
            AesHashFinalSeed0 = seed.u2 ^ 0x3C6EF372FE94F82BUL;
            AesHashFinalSeed1 = seed.u3 ^ 0xA54FF53A5F1D36F1UL;
        }

        private const ulong FinalizerDomain = 0x9E3779B97F4A7C15UL;

        /// <inheritdoc />
        /// <remarks>
        /// NH, the UMAC/VMAC core, over 32-bit lanes: one product per key word. The guest's target has no
        /// widening multiply reachable from here, so <see cref="MultiplyFold"/> costs four muls and a
        /// shift chain, while a 32x32-&gt;64 product is a single mul.
        /// </remarks>
        // Keep a call boundary: the RISC-V backend can omit the int truncation after an inlined mixer.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static partial ulong MixWords(ulong u0, ulong u1, ulong u2, ulong u3, ref ulong seeds)
        {
            ulong sum = MixLanes(u0, seeds)
                + MixLanes(u1, Unsafe.Add(ref seeds, 1))
                + MixLanes(u2, Unsafe.Add(ref seeds, 2))
                + MixLanes(u3, Unsafe.Add(ref seeds, 3));
            // NH carries its entropy high and a dictionary buckets on the low bits, so the sum needs a
            // finalizer. Each step is a bijection, so it moves bits without adding collisions of its own.
            ulong hash = sum ^ (sum >> 31);
            hash *= HashFinalizerKey;
            return hash ^ (hash >> 29);
        }

        /// <summary>Keys both 32-bit lanes of a key word and multiplies them into one 64-bit product.</summary>
        /// <remarks>
        /// The lane keys have to stay independent per position: sharing one across words would leave the
        /// sum invariant under permuting them.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MixLanes(ulong word, ulong key)
            => (ulong)(uint)((uint)word + (uint)key) * (uint)((uint)(word >> 32) + (uint)(key >> 32));

        /// <inheritdoc />
        /// <remarks>Hand-rolled from 32-bit products: ILC has no <c>mulhu</c> intrinsic on this target.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static partial ulong MultiplyFold(ulong a, ulong b)
        {
            uint al = (uint)a, ah = (uint)(a >> 32);
            uint bl = (uint)b, bh = (uint)(b >> 32);
            ulong lower = (ulong)al * bl;
            ulong middle = (ulong)ah * bl + (lower >> 32);
            ulong carry = (ulong)al * bh + (uint)middle;
            ulong low = (carry << 32) | (uint)lower;
            ulong high = (ulong)ah * bh + (middle >> 32) + (carry >> 32);
            return low ^ high;
        }
    }
}
