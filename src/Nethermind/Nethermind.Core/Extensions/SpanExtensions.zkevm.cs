// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
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
        // ILC turns a constant uint mask into two shifts per lane; a seeded field keeps one mask in a register.
        private static ulong HashLaneMask;
        private static ulong[]? AddressSeeds;
        private static ulong[]? ShortHashSeeds;
        // One key per slot-index word MixSlot reads.
        private static ulong[]? SlotSeeds;
        // MixSlot's last input and result: one storage access hashes the same cell for each map it probes. Unsynchronised,
        // as the guest hashes on one thread.
        private static ulong LastSlotAddressSum;
        private static ulong LastSlot0;
        private static ulong LastSlot1;
        private static ulong LastSlot2;
        private static ulong LastSlot3;
        private static ulong LastSlotHash;

        /// <inheritdoc />
        public static partial void SeedHashes(in Int256.UInt256 seed)
        {
            InstanceRandom = seed;
            // Installed before anything mixes, which CreateShortHashSeeds below does.
            HashLaneMask = uint.MaxValue;
            HashFinalizerKey = MultiplyFold(seed.u0 ^ FinalizerDomain, seed.u3 ^ ~FinalizerDomain) | 1UL;
            ShortHashSeeds = CreateShortHashSeeds(in InstanceRandom);
            AddressSeeds = [DeriveAddressSeed(seed.u0), DeriveAddressSeed(seed.u1),
                DeriveAddressSeed(seed.u2), DeriveAddressSeed(seed.u3)];
            SlotSeeds = [DeriveSlotSeed(seed.u0, 0), DeriveSlotSeed(seed.u1, 1), DeriveSlotSeed(seed.u2, 2), DeriveSlotSeed(seed.u3, 3)];
            // The memo's all-zero key must map to what MixSlot would compute for it.
            LastSlotAddressSum = LastSlot0 = LastSlot1 = LastSlot2 = LastSlot3 = 0;
            LastSlotHash = SumSlot(0, 0, 0, 0, 0);
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
            Debug.Assert(HashLaneMask != 0, $"{nameof(SeedHashes)} must run before hashing.");
            ulong mask = HashLaneMask;
            ulong sum = MixLanes(u0, seeds, mask)
                + MixLanes(u1, Unsafe.Add(ref seeds, 1), mask)
                + MixLanes(u2, Unsafe.Add(ref seeds, 2), mask)
                + MixLanes(u3, Unsafe.Add(ref seeds, 3), mask);
            return FinalizeSum(sum);
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static partial ulong MixAddress(ref byte b) => FinalizeSum(SumAddressLanes(ref b));

        /// <summary>The NH sum of a 20-byte address' words, before the finalizer.</summary>
        /// <remarks>What <see cref="MixAddress"/> finalizes and <see cref="MixSlot"/> extends, for a caller keeping it.</remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ulong SumAddressWords(ref byte address) => SumAddressLanes(ref address);

        /// <summary>Finalizes a <see cref="SumAddressWords"/> result into the hash <see cref="MixAddress"/> returns.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ulong FinalizeAddressSum(ulong addressSum) => FinalizeSum(addressSum);

        /// <summary>Hashes a 32-byte slot index of the address whose <see cref="SumAddressWords"/> is given.</summary>
        /// <remarks>
        /// One NH sum over the address' three words and the index's four, so an address kept summed costs only its
        /// index. The address words are keyed apart from the index words, which NH's bound requires.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ulong MixSlot(ulong addressSum, ref byte index)
        {
            ulong i0 = Unsafe.ReadUnaligned<ulong>(ref index);
            ulong i1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref index, 8));
            ulong i2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref index, 16));
            ulong i3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref index, 24));
            if (addressSum == LastSlotAddressSum && i0 == LastSlot0 && i1 == LastSlot1 && i2 == LastSlot2 && i3 == LastSlot3)
                return LastSlotHash;

            ulong hash = SumSlot(addressSum, i0, i1, i2, i3);
            LastSlotAddressSum = addressSum;
            LastSlot0 = i0;
            LastSlot1 = i1;
            LastSlot2 = i2;
            LastSlot3 = i3;
            LastSlotHash = hash;
            return hash;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong SumSlot(ulong addressSum, ulong i0, ulong i1, ulong i2, ulong i3)
        {
            Debug.Assert(SlotSeeds is not null, $"{nameof(SeedHashes)} must run before hashing.");
            ulong mask = HashLaneMask;
            ref ulong seeds = ref MemoryMarshal.GetArrayDataReference(SlotSeeds!);
            ulong sum = addressSum
                + MixLanes(i0, seeds, mask)
                + MixLanes(i1, Unsafe.Add(ref seeds, 1), mask)
                + MixLanes(i2, Unsafe.Add(ref seeds, 2), mask)
                + MixLanes(i3, Unsafe.Add(ref seeds, 3), mask);
            return FinalizeSum(sum);
        }

        // NH over three words, the last ending at the address' last byte and so overlapping the middle one: the words
        // stay an injective function of the address, and a whole-word load replaces a narrow one.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong SumAddressLanes(ref byte b)
        {
            Debug.Assert(AddressSeeds is not null, $"{nameof(SeedHashes)} must run before hashing.");
            ulong mask = HashLaneMask;
            ref ulong seeds = ref MemoryMarshal.GetArrayDataReference(AddressSeeds!);
            return MixLanes(Unsafe.ReadUnaligned<ulong>(ref b), seeds, mask)
                + MixLanes(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8)), Unsafe.Add(ref seeds, 1), mask)
                + MixLanes(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, Address.Size - sizeof(ulong))), Unsafe.Add(ref seeds, 2), mask);
        }

        /// <summary>Spreads an NH sum's high bits into the low ones a dictionary buckets on.</summary>
        /// <remarks>Each step is a bijection, so it moves bits without adding collisions of its own.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong FinalizeSum(ulong sum)
        {
            ulong hash = sum ^ (sum >> 31);
            hash *= HashFinalizerKey;
            return hash ^ (hash >> 29);
        }

        // Keys of their own, apart from the address keys MixSlot adds to: NH's bound assumes each word's key is
        // independent of the others'.
        private static ulong DeriveSlotSeed(ulong word, int lane)
        {
            word += 0xA0761D6478BD642FUL * (ulong)(lane + 1);
            word = (word ^ (word >> 30)) * 0xBF58476D1CE4E5B9UL;
            word = (word ^ (word >> 27)) * 0x94D049BB133111EBUL;
            return word ^ (word >> 31);
        }

        /// <summary>Keys both 32-bit lanes of a key word and multiplies them into one 64-bit product.</summary>
        /// <remarks>
        /// The lane keys have to stay independent per position: sharing one across words would leave the
        /// sum invariant under permuting them.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MixLanes(ulong word, ulong key, ulong mask)
            => ((word + key) & mask) * (((word >> 32) + (key >> 32)) & mask);

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
