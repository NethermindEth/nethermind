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
        public static readonly Int256.UInt256 InstanceRandom = new(CreateSeed(32, purpose: 0));

        private static readonly ulong[] AddressSeeds = [DeriveAddressSeed(InstanceRandom.u0), DeriveAddressSeed(InstanceRandom.u1),
            DeriveAddressSeed(InstanceRandom.u2), DeriveAddressSeed(InstanceRandom.u3)];

        private static readonly ulong[] ShortHashSeeds = CreateShortHashSeeds(in InstanceRandom);

        /// <inheritdoc />
        /// <remarks>The host draws its own seed above, per process; the argument is the guest's.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static partial void SeedHashes(in Int256.UInt256 seed) { }

        /// <inheritdoc />
        /// <remarks>Mixes each seed limb into its key limb before any information is lost to folding.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static partial ulong MixWords(ulong u0, ulong u1, ulong u2, ulong u3, ref ulong seeds)
        {
            ulong a = MultiplyFold(u0 ^ seeds, u1 ^ Unsafe.Add(ref seeds, 1));
            ulong b = MultiplyFold(u2 ^ Unsafe.Add(ref seeds, 2), u3 ^ Unsafe.Add(ref seeds, 3));
            return (ulong)MumFold(a, b);
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static partial ulong MixAddress(ref byte b)
        {
            AssertSeeded();
            ref ulong seeds = ref MemoryMarshal.GetArrayDataReference(AddressSeeds!);
            return MixWords(
                Unsafe.ReadUnaligned<ulong>(ref b),
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8)),
                Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref b, 16)), 0, ref seeds);
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static partial ulong MultiplyFold(ulong a, ulong b)
        {
            ulong high = Math.BigMul(a, b, out ulong low);
            return low ^ high;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static partial int CombineHash(uint hash, ulong value) => (int)BitOperations.Crc32C(hash, value);

        private static readonly Vector128<byte> AesHashSeed = CreateAesHashSeed(purpose: 1);
        private static readonly Vector128<byte> AesHash20Seed = CreateAesHashSeed(purpose: 2);
        private static readonly Vector128<byte> AesHashPairSeed = CreateAesHashSeed(purpose: 3);
        private static readonly Vector128<byte> AesHash32Seed = CreateAesHashSeed(purpose: 4);
        private static readonly Vector128<byte> AesHashFinalSeed = CreateAesHashSeed(purpose: 5);

        private static Vector128<byte> CreateAesHashSeed(int purpose) =>
            Unsafe.ReadUnaligned<Vector128<byte>>(ref MemoryMarshal.GetArrayDataReference(CreateSeed(16, purpose)));

        /// <summary>
        /// Cryptographic randomness, or in a <see cref="DeterministicBenchmark"/> a seed fixed per purpose: hash tables
        /// then lay out the same way on every run, so a block's instruction count repeats.
        /// </summary>
        private static byte[] CreateSeed(int length, int purpose) => DeterministicBenchmark.Enabled
            ? System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"deterministic-benchmark/{purpose}"))[..length]
            : System.Security.Cryptography.RandomNumberGenerator.GetBytes(length);

    }
}
