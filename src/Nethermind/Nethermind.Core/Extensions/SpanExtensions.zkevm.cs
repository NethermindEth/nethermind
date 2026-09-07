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
        private static ulong[]? AddressSeeds;
        private static ulong[]? ShortHashSeeds;

        /// <inheritdoc />
        public static partial void SeedHashes(in Int256.UInt256 seed)
        {
            InstanceRandom = seed;
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
    }
}
