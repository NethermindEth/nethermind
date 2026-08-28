// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class Hash256KademliaDistanceTests
{
    private static readonly Hash256KademliaDistance Distance = Hash256KademliaDistance.Instance;

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
        Hash256 xor = XorDistance(new(hash1), new(hash2));
        Assert.That(xor.ToString(), Is.EqualTo(xosString.ToLower()));
        Assert.That(Distance.CalculateLogDistance(new Hash256(hash1), new Hash256(hash2)), Is.EqualTo(expectedDistance));
        Assert.That(Distance.CalculateLogDistance(new Hash256(hash2), new Hash256(hash1)), Is.EqualTo(expectedDistance));
    }

    [Test]
    public void TestGetRandomHash()
    {
        Random rand = new(0);
        Span<byte> randomizedBytes = stackalloc byte[Hash256.Size];
        rand.NextBytes(randomizedBytes);
        Hash256 randomized = new(randomizedBytes);

        void TestForDistance(int distance)
        {
            Hash256 randHash = Distance.GetRandomHashAtDistance(randomized, distance, rand);
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
        Hash256 hash = new("0x0000000000000000000000000000000000000000000000000000000000000000");

        Assert.That(() => Distance.GetRandomHashAtDistance(hash, distance, new Random(0)), Throws.InstanceOf<ArgumentOutOfRangeException>());
    }

    [TestCase]
    public void TestDistanceCompare()
    {
        Hash256 h1 = new("0x0010000000000000000000000000000000000000000000000000000000000000");
        Hash256 h2 = new("0x0110000000000000000000000000000000000000000000000000000000000000");
        Hash256 h3 = new("0x0000000000000000000000000000000000000000000000000000000000000000");

        Assert.That(Distance.Compare(h1, h2, h3), Is.LessThan(0));
    }

    [Test]
    public void ValueHash_operations_match_reference_hash_operations()
    {
        Hash256 left = new("0x0010000000000000000000000000000000000000000000000000000000000001");
        Hash256 right = new("0x0110000000000000000000000000000000000000000000000000000000000002");
        Hash256 target = new("0x0000000000000000000000000000000000000000000000000000000000000003");
        IKademliaDistance<ValueHash256> valueDistance = Distance;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valueDistance.Zero, Is.EqualTo(default(ValueHash256)));
            Assert.That(
                valueDistance.CalculateLogDistance(left.ValueHash256, right.ValueHash256),
                Is.EqualTo(Distance.CalculateLogDistance(left, right)));
            Assert.That(
                valueDistance.Compare(left.ValueHash256, right.ValueHash256, target.ValueHash256),
                Is.EqualTo(Distance.Compare(left, right, target)));
        }

        for (int i = 0; i < Distance.MaxDistance; i++)
        {
            Assert.That(valueDistance.GetBit(left.ValueHash256, i), Is.EqualTo(Distance.GetBit(left, i)));
        }

        foreach (int index in new[] { 0, 127, 255 })
        {
            Assert.That(
                valueDistance.SetBit(left.ValueHash256, index),
                Is.EqualTo(Distance.SetBit(left, index).ValueHash256));
        }
    }

    private static Hash256 XorDistance(Hash256 left, Hash256 right)
    {
        Span<byte> result = stackalloc byte[Hash256.Size];
        ReadOnlySpan<byte> leftBytes = left.Bytes;
        ReadOnlySpan<byte> rightBytes = right.Bytes;
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (byte)(leftBytes[i] ^ rightBytes[i]);
        }

        return new Hash256(result);
    }
}
