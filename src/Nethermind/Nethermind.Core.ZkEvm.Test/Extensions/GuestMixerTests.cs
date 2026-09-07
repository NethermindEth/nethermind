// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Extensions;

/// <summary>
/// Bucket-window distribution for the guest's scalar hash mixers, which the host suite cannot reach:
/// <see cref="SpanExtensions.FastHash64For20Bytes"/> picks the AES path whenever the hardware has AES,
/// so these fallbacks stay unexercised even in a ZK_EVM build running on x64.
/// </summary>
/// <remarks>
/// Windows and thresholds mirror <c>AssertHash64WindowsAreDistributed</c> in
/// <c>Nethermind.Core.Test/BytesTests.cs</c>, because these hashes end up in the same bucketed caches.
/// The counter sweep checks that entropy at every aligned offset reaches all bucket windows.
/// </remarks>
[NonParallelizable]
public class GuestMixerTests
{
    private const int SampleCount = 4096;

    public static IEnumerable<TestCaseData> CounterOffsets()
    {
        foreach (int length in new[] { 20, 32 })
        {
            for (int offset = 0; offset + sizeof(uint) <= length; offset += sizeof(uint))
            {
                yield return new TestCaseData(length, offset).SetName(
                    $"Guest_mixer_distributes_{length}_byte_keys_with_entropy_at_offset_{offset}");
            }
        }
    }

    [TestCaseSource(nameof(CounterOffsets))]
    public void Guest_mixer_distributes_bucket_windows(int length, int offset)
    {
        byte[] input = new byte[length];
        long[] hashes = new long[SampleCount];

        for (uint value = 0; value < SampleCount; value++)
        {
            input.AsSpan().Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(offset), value);
            ref byte start = ref MemoryMarshal.GetArrayDataReference(input);
            hashes[value] = length == 20
                ? SpanExtensions.FastHash64For20BytesFallback(ref start)
                : SpanExtensions.FastHash64For32BytesFallback(ref start);
        }

        AssertWindowsAreDistributed(hashes, $"{length}-byte keys, entropy at offset {offset}");
    }

    /// <summary>Checks that width-specific seeds distinguish addresses from zero-padded words.</summary>
    [Test]
    public void Guest_mixer_separates_an_address_from_its_zero_padded_word()
    {
        byte[] address = new byte[20];
        byte[] padded = new byte[32];
        for (int i = 0; i < address.Length; i++)
        {
            address[i] = (byte)(0xA0 + i);
            padded[i] = address[i];
        }

        long addressHash = SpanExtensions.FastHash64For20BytesFallback(
            ref MemoryMarshal.GetArrayDataReference(address));
        long paddedHash = SpanExtensions.FastHash64For32BytesFallback(
            ref MemoryMarshal.GetArrayDataReference(padded));

        Assert.That(addressHash, Is.Not.EqualTo(paddedHash));
    }

    private static readonly UInt256 SecondSeed = new(0x219B4AD604915E33UL, 0x28811B0595AE539EUL,
        0x5D38E6AFF0752500UL, 0xC8AEAC7F08A75C3DUL);

    /// <summary>Restores the assembly seed after tests that change process-wide state.</summary>
    [TearDown]
    public void RestoreSeed() => SpanExtensions.SeedHashes(SeedGuestHashes.Seed);

    /// <summary>Guards against class-initialisation checks on the guest's hash calls.</summary>
    [Test]
    public void Guest_hash_type_has_no_class_constructor() =>
        Assert.That(typeof(SpanExtensions).TypeInitializer, Is.Null);

    /// <summary>Provides key widths and every bit of the full-width seed.</summary>
    public static IEnumerable<TestCaseData> SeedBits()
    {
        foreach (int width in new[] { 7, 16, 20, 32, 48, 64, 80 })
            for (int bit = 0; bit < 256; bit++)
                yield return new TestCaseData(width, bit);
    }

    /// <summary>Checks that the mixers retain every seed bit.</summary>
    [TestCaseSource(nameof(SeedBits))]
    public void Guest_mixer_uses_every_seed_bit(int width, int bit)
    {
        byte[] key = new byte[width];
        key.AsSpan().Fill(0xAB);
        SpanExtensions.SeedHashes(SeedGuestHashes.Seed);
        long before = Hash(key);
        long publicBefore = PublicHash(key);
        UInt256 slot = new(0xAB, 0xCD, 0xEF, 0x01);
        int slotBefore = UInt256Comparer.Instance.GetHashCode(slot);

        UInt256 changed = SeedGuestHashes.Seed ^ (UInt256.One << bit);
        SpanExtensions.SeedHashes(changed);
        long after = Hash(key);
        long publicAfter = PublicHash(key);
        int slotAfter = UInt256Comparer.Instance.GetHashCode(slot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(after, Is.Not.EqualTo(before), "changed seed bit");
            Assert.That(publicAfter, Is.Not.EqualTo(publicBefore), "public hash uses every seed bit");
            Assert.That(slotAfter, Is.Not.EqualTo(slotBefore), "The guest slot comparer receives every seed bit");
            Assert.That(SpanExtensions.InstanceRandom, Is.EqualTo(changed), "full-width seed retained");
        }
    }

    /// <summary>Checks distribution for keys related by paired high-bit changes.</summary>
    [TestCase(20)]
    [TestCase(32)]
    [TestCase(48)]
    public void Guest_mixer_separates_paired_high_bit_changes(int width)
    {
        byte[] key = new byte[width];
        foreach (UInt256 seed in new[] { SeedGuestHashes.Seed, SecondSeed })
        {
            SpanExtensions.SeedHashes(seed);
            for (ulong value = 0; value < 256; value++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(key, value);
                BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8), value * 7);
                long before = Hash(key);
                key[7] ^= 0x80;
                key[15] ^= 0x80;
                Assert.That(Hash(key), Is.Not.EqualTo(before), $"value {value}");
            }
        }
    }

    /// <summary>Checks that an independently replaced seed disperses a seed-specific collision set.</summary>
    [Test]
    public void Guest_mixer_reseeding_breaks_a_collision_set()
    {
        HashSet<long> before = [];
        HashSet<long> after = [];
        byte[] key = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(key, SeedGuestHashes.Seed.u0);
        for (ulong value = 0; value < 4096; value++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8), value);
            SpanExtensions.SeedHashes(SeedGuestHashes.Seed);
            before.Add(Hash(key));
            SpanExtensions.SeedHashes(SecondSeed);
            after.Add(Hash(key));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(before.Count, Is.EqualTo(1), "constructed collision set");
            Assert.That(after.Count, Is.GreaterThan(4064), "replacement seed");
        }
    }

    /// <summary>Checks the 32-byte mixer against an independent widening-product reference.</summary>
    [Test]
    public void Guest_word_mixer_matches_reference()
    {
        UInt256 seed = SeedGuestHashes.Seed;
        SpanExtensions.SeedHashes(seed);
        foreach (UInt256 value in new[] { UInt256.Zero, UInt256.One, UInt256.MaxValue, SecondSeed })
        {
            ulong a = ReferenceFold(value.u0 ^ seed.u0, value.u1 ^ seed.u1);
            ulong b = ReferenceFold(value.u2 ^ seed.u2, value.u3 ^ seed.u3);
            ulong expected = ReferenceFold(a ^ 0x9E3779B97F4A7C15UL, b ^ 0xBF58476D1CE4E5B9UL);
            byte[] bytes = value.ToLittleEndian();
            using (Assert.EnterMultipleScope())
            {
                Assert.That((ulong)Hash(bytes), Is.EqualTo(expected));
                Assert.That(SpanExtensions.FastHashFallback(bytes), Is.EqualTo(unchecked((int)(expected ^ (expected >> 32)))));
            }
        }
    }

    /// <summary>Checks variable-length tails, block boundaries, and trailing zero bytes.</summary>
    [Test]
    public void Guest_mixer_includes_length_and_every_byte()
    {
        HashSet<int> paddedHashes = [];
        SpanExtensions.SeedHashes(SeedGuestHashes.Seed);
        for (int length = 1; length <= 80; length++)
        {
            byte[] bytes = new byte[length];
            int original = SpanExtensions.FastHashFallback(bytes);
            paddedHashes.Add(original);
            for (int offset = 0; offset < length; offset++)
            {
                bytes[offset] = 1;
                Assert.That(SpanExtensions.FastHashFallback(bytes), Is.Not.EqualTo(original), $"length {length}, offset {offset}");
                bytes[offset] = 0;
            }
        }
        Assert.That(paddedHashes.Count, Is.EqualTo(80));
    }

    /// <summary>Checks that installing the same seed again reproduces its hashes.</summary>
    [Test]
    public void Guest_mixer_seed_replaces_previous_state()
    {
        byte[] key = new byte[32];
        SpanExtensions.SeedHashes(SeedGuestHashes.Seed);
        long first = Hash(key);
        SpanExtensions.SeedHashes(SecondSeed);
        SpanExtensions.SeedHashes(SeedGuestHashes.Seed);
        Assert.That(Hash(key), Is.EqualTo(first));
    }

    /// <summary>Checks that guest slot containers select the explicitly seeded comparer.</summary>
    [Test]
    public void Guest_slot_comparer_hashes_through_the_mixer()
    {
        UInt256 slot = new(0xAB, 0xCD, 0xEF, 0x01);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(UInt256Comparer.GetOptimized(), Is.SameAs(UInt256Comparer.Instance));
            Assert.That(UInt256Comparer.Instance.GetHashCode(slot), Is.EqualTo(((ReadOnlySpan<byte>)slot.ToLittleEndian()).FastHash()));
        }
    }

    [TestCase(0, -220954673)]
    [TestCase(1, 1365513433)]
    [TestCase(2, 1215851697)]
    [TestCase(3, 1062475265)]
    [TestCase(4, 1800791587)]
    [TestCase(5, -1439988632)]
    [TestCase(6, 211467597)]
    [TestCase(7, 1321382043)]
    [TestCase(8, -128984591)]
    [TestCase(9, 1383455619)]
    [TestCase(10, -1217756578)]
    [TestCase(11, -2027848051)]
    [TestCase(12, -1289996763)]
    [TestCase(13, 751476928)]
    [TestCase(14, -2043828144)]
    [TestCase(15, -308078777)]
    [TestCase(16, -1699459048)]
    [TestCase(17, -1754749950)]
    [TestCase(31, -1045709015)]
    [TestCase(33, 1762330450)]
    [TestCase(63, 1350296845)]
    [TestCase(64, -1610973583)]
    [TestCase(65, -1918792359)]
    public void Scalar_hash_preserves_tail_and_block_boundary_vectors(int length, int expected)
    {
        SpanExtensions.SeedHashes(new UInt256(0x243F6A8885A308D3UL, 0x13198A2E03707344UL,
            0xA4093822299F31D0UL, 0x082EFA98EC4E6C89UL));
        byte[] input = new byte[length];
        for (int i = 0; i < input.Length; i++) input[i] = (byte)(i * 37 + 11);

        Assert.That(SpanExtensions.FastHashFallback(input), Is.EqualTo(expected));
    }

    [Test]
    public void Address_hashes_agree_across_dispatch_paths()
    {
        SpanExtensions.SeedHashes(SeedGuestHashes.Seed);
        byte[] bytes = new byte[Address.Size];
        Random random = new(42);
        Dictionary<AddressAsKey, int> keys = [];
        for (int i = 0; i < 256; i++)
        {
            random.NextBytes(bytes);
            Address address = new(bytes);
            AddressAsKey key = address;
            keys[key] = i;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(key.GetHashCode(), Is.EqualTo(BoxedHash(key)));
                Assert.That(key.GetHashCode(), Is.EqualTo(BoxedHash(address)));
                Assert.That(keys[new Address(bytes)], Is.EqualTo(i));
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int BoxedHash(object key) => key.GetHashCode();
    }

    private static IEnumerable<ulong> ChainedHashes() =>
        [0UL, 0xBF58476D1CE4E5B9UL, ulong.MaxValue, SeedGuestHashes.Seed.u0 ^ 0xBF58476D1CE4E5B9UL];

    [TestCaseSource(nameof(ChainedHashes))]
    public void Chained_hash_includes_the_key_and_both_halves_of_the_previous_hash(ulong previousHash)
    {
        ValueHash256 first = new(SeedGuestHashes.Seed.ToLittleEndian());
        ValueHash256 second = new(SecondSeed.ToLittleEndian());
        int original = first.GetChainedHashCode(previousHash);
        Assert.That(second.GetChainedHashCode(previousHash), Is.Not.EqualTo(original));
        for (int bit = 0; bit < 64; bit++)
            Assert.That(first.GetChainedHashCode(previousHash ^ (1UL << bit)), Is.Not.EqualTo(original), $"bit {bit}");
    }

    private static long Hash(byte[] key)
    {
        ref byte start = ref MemoryMarshal.GetArrayDataReference(key);
        return key.Length switch
        {
            20 => SpanExtensions.FastHash64For20BytesFallback(ref start),
            32 => SpanExtensions.FastHash64For32BytesFallback(ref start),
            _ => SpanExtensions.FastHashFallback(key)
        };
    }

    private static long PublicHash(byte[] key)
    {
        ref byte start = ref MemoryMarshal.GetArrayDataReference(key);
        return key.Length switch
        {
            20 => SpanExtensions.FastHash64For20Bytes(ref start),
            32 => SpanExtensions.FastHash64For32Bytes(ref start),
            _ => ((ReadOnlySpan<byte>)key).FastHash()
        };
    }

    private static ulong ReferenceFold(ulong a, ulong b)
    {
        BigInteger product = (BigInteger)a * b;
        return (ulong)(product & ulong.MaxValue) ^ (ulong)(product >> 64);
    }

    private static void AssertWindowsAreDistributed(long[] hashes, string context)
    {
        HashSet<long> fullHashes = new(hashes.Length);
        HashSet<int> way0Sets = new(hashes.Length);
        HashSet<int> signatures = new(hashes.Length);
        HashSet<int> way1Sets = new(hashes.Length);

        foreach (long hash in hashes)
        {
            ulong bits = (ulong)hash;
            fullHashes.Add(hash);
            way0Sets.Add((int)(bits & 0x3FFF));
            signatures.Add((int)((bits >> 22) & 0xF_FFFF));
            way1Sets.Add((int)((bits >> 42) & 0x3FFF));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fullHashes.Count, Is.GreaterThan(hashes.Length - 32), $"{context}: full hash");
            Assert.That(way0Sets.Count, Is.GreaterThan(hashes.Length * 3 / 4), $"{context}: bits 0-13");
            Assert.That(signatures.Count, Is.GreaterThan(hashes.Length - 32), $"{context}: bits 22-41");
            Assert.That(way1Sets.Count, Is.GreaterThan(hashes.Length * 3 / 4), $"{context}: bits 42-55");
        }
    }
}
