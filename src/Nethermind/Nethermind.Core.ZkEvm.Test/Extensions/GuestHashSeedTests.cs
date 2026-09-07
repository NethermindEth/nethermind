// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Extensions;

/// <summary>Checks full-width seed installation and guest mixer invariants.</summary>
[NonParallelizable]
public class GuestHashSeedTests
{
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
        foreach (int width in new[] { 7, 20, 32, 48 })
            for (int bit = 0; bit < 256; bit++)
                yield return new TestCaseData(width, bit);
    }

    /// <summary>Checks that both seed overloads retain every input bit.</summary>
    [TestCaseSource(nameof(SeedBits))]
    public void Guest_mixer_uses_every_seed_bit(int width, int bit)
    {
        byte[] key = new byte[width];
        key.AsSpan().Fill(0xAB);
        SpanExtensions.SeedHashes(SeedGuestHashes.Seed);
        long before = Hash(key);

        UInt256 changed = SeedGuestHashes.Seed ^ (UInt256.One << bit);
        Span<byte> bytes = stackalloc byte[32];
        changed.ToLittleEndian(bytes);
        SpanExtensions.SeedHashes(new ValueHash256(bytes));
        long after = Hash(key);
        SpanExtensions.SeedHashes(changed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(after, Is.Not.EqualTo(before), "changed seed bit");
            Assert.That(Hash(key), Is.EqualTo(after), "equivalent seed overloads");
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

    private static ulong ReferenceFold(ulong a, ulong b)
    {
        BigInteger product = (BigInteger)a * b;
        return (ulong)(product & ulong.MaxValue) ^ (ulong)(product >> 64);
    }
}
