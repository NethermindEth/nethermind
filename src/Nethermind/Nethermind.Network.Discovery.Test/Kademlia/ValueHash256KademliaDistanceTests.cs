// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Kademlia;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class ValueHash256KademliaDistanceTests
{
    private static readonly ValueHash256KademliaDistance Distance = ValueHash256KademliaDistance.Instance;

    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000",
              "0x0000000000000000000000000000000000000000000000000000000000000000",
              "0x0000000000000000000000000000000000000000000000000000000000000000", 0)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000",
              "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
              "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", 256)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000",
              "0xf000000000000000000000000000000000000000000000000000000000000000",
              "0xf000000000000000000000000000000000000000000000000000000000000000", 256)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000",
              "0xe000000000000000000000000000000000000000000000000000000000000000",
              "0xe000000000000000000000000000000000000000000000000000000000000000", 256)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000",
              "0x7000000000000000000000000000000000000000000000000000000000000000",
              "0x7000000000000000000000000000000000000000000000000000000000000000", 255)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000",
              "0x0f00000000000000000000000000000000000000000000000000000000000000",
              "0x0f00000000000000000000000000000000000000000000000000000000000000", 252)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000",
              "0x0e00000000000000000000000000000000000000000000000000000000000000",
              "0x0e00000000000000000000000000000000000000000000000000000000000000", 252)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000",
              "0x0700000000000000000000000000000000000000000000000000000000000000",
              "0x0700000000000000000000000000000000000000000000000000000000000000", 251)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000",
              "0x000e000000000000000000000000000000000000000000000000000000000000",
              "0x000e000000000000000000000000000000000000000000000000000000000000", 244)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000",
              "0x000000000000000000000000000000000000000000000000000000000000000f",
              "0x000000000000000000000000000000000000000000000000000000000000000f", 4)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000",
              "0x0000000000000000000000000000000000000000000000000000000000f0000f",
              "0x0000000000000000000000000000000000000000000000000000000000f0000f", 24)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000",
              "0x00000000000000000000000000000000000000000000000000000000000f000f",
              "0x00000000000000000000000000000000000000000000000000000000000f000f", 20)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000",
              "0x000000000000000000000000000000000000000000000000000000000001000f",
              "0x000000000000000000000000000000000000000000000000000000000001000f", 17)]
    public void TestDistance(string hash1, string hash2, string xosString, int expectedDistance)
    {
        ValueHash256 xor = XorDistance(new(hash1), new(hash2));
        Assert.That(xor.ToString(), Is.EqualTo(xosString.ToLower()));
        Assert.That(Distance.CalculateLogDistance(new ValueHash256(hash1), new ValueHash256(hash2)), Is.EqualTo(expectedDistance));
        Assert.That(Distance.CalculateLogDistance(new ValueHash256(hash2), new ValueHash256(hash1)), Is.EqualTo(expectedDistance));
    }

    [Test]
    public void TestGetRandomHash()
    {
        Random rand = new(0);
        Span<byte> randomizedBytes = stackalloc byte[ValueHash256.MemorySize];
        rand.NextBytes(randomizedBytes);
        ValueHash256 randomized = new(randomizedBytes);

        void TestForDistance(int distance)
        {
            ValueHash256 randHash = Distance.GetRandomHashAtDistance(randomized, distance, rand);
            Assert.That(Distance.CalculateLogDistance(randomized, randHash), Is.EqualTo(distance));
        }

        for (int i = 0; i <= 256; i++)
        {
            rand = new(0);
            for (int j = 0; j < 10; j++)
            {
                TestForDistance(i);
            }
        }

    }

    [TestCase(-1)]
    [TestCase(257)]
    public void GetRandomHashAtDistance_ShouldRejectInvalidDistance(int distance)
    {
        ValueHash256 hash = new("0x0000000000000000000000000000000000000000000000000000000000000000");

        Assert.That(() => Distance.GetRandomHashAtDistance(hash, distance, new Random(0)), Throws.InstanceOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void TestDistanceCompare()
    {
        ValueHash256 h1 = new("0x0010000000000000000000000000000000000000000000000000000000000000");
        ValueHash256 h2 = new("0x0110000000000000000000000000000000000000000000000000000000000000");
        ValueHash256 target = h2;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Distance.Compare(h1, h2, target), Is.GreaterThan(0));
            Assert.That(Distance.Compare(h2, h1, target), Is.LessThan(0));
            Assert.That(Distance.Compare(h1, h1, target), Is.Zero);
        }
    }

    [Test]
    public void ValueHash_bit_operations_cover_the_full_key_space()
    {
        Assert.That(Distance.Zero, Is.EqualTo(default(ValueHash256)));

        Span<byte> expectedBytes = stackalloc byte[ValueHash256.MemorySize];
        for (int index = 0; index < Distance.MaxDistance; index++)
        {
            expectedBytes.Clear();
            expectedBytes[index / 8] = (byte)(1 << (7 - (index % 8)));
            ValueHash256 expected = new(expectedBytes);
            ValueHash256 actual = Distance.SetBit(default, index);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual, Is.EqualTo(expected));
                Assert.That(Distance.GetBit(actual, index), Is.True);
                Assert.That(Distance.CalculateLogDistance(default, actual), Is.EqualTo(Distance.MaxDistance - index));
            }
        }
    }

    private static ValueHash256 XorDistance(ValueHash256 left, ValueHash256 right)
    {
        Span<byte> result = stackalloc byte[ValueHash256.MemorySize];
        ReadOnlySpan<byte> leftBytes = left.Bytes;
        ReadOnlySpan<byte> rightBytes = right.Bytes;
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (byte)(leftBytes[i] ^ rightBytes[i]);
        }

        return new ValueHash256(result);
    }
}
