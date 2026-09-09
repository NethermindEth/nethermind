// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Nethermind.Core.Extensions
{
    public static partial class SpanExtensions
    {
        // Ensure that hashes are different for every run of the node and every node, so if there are any hash collisions
        // on one node, they will not be the same on another node or across a restart and cannot degrade the network as a whole.
        /// <summary>The full-width process seed from cryptographic randomness.</summary>
        public static readonly Int256.UInt256 InstanceRandom =
            new(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        private static readonly ulong[] AddressSeeds = [DeriveAddressSeed(InstanceRandom.u0), DeriveAddressSeed(InstanceRandom.u1),
            DeriveAddressSeed(InstanceRandom.u2), DeriveAddressSeed(InstanceRandom.u3)];

        private static readonly ulong[] ShortHashSeeds = CreateShortHashSeeds(in InstanceRandom);

        /// <inheritdoc />
        /// <remarks>The host draws its own seed above, per process; the argument is the guest's.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static partial void SeedHashes(in Int256.UInt256 seed) { }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static partial int CombineHash(uint hash, ulong value) => (int)BitOperations.Crc32C(hash, value);

        private static readonly Vector128<byte> AesHashSeed = CreateAesHashSeed();
        private static readonly Vector128<byte> AesHash20Seed = CreateAesHashSeed();
        private static readonly Vector128<byte> AesHashPairSeed = CreateAesHashSeed();
        private static readonly Vector128<byte> AesHash32Seed = CreateAesHashSeed();
        private static readonly Vector128<byte> AesHashFinalSeed = CreateAesHashSeed();

        [SkipLocalsInit]
        private static Vector128<byte> CreateAesHashSeed()
        {
            Span<byte> bytes = stackalloc byte[16];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            return Unsafe.ReadUnaligned<Vector128<byte>>(ref MemoryMarshal.GetReference(bytes));
        }

    }
}
