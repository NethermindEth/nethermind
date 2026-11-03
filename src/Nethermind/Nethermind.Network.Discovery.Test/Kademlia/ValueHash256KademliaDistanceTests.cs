// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(xor.ToString(), Is.EqualTo(xosString.ToLower()));
            Assert.That(Distance.CalculateLogDistance(new ValueHash256(hash1), new ValueHash256(hash2)), Is.EqualTo(expectedDistance));
            Assert.That(Distance.CalculateLogDistance(new ValueHash256(hash2), new ValueHash256(hash1)), Is.EqualTo(expectedDistance));
        }
    }

    [Test]
    public void RandomHashAtDistance_HasRequestedDistance()
    {
        Random rand = new(0);
        Span<byte> randomizedBytes = stackalloc byte[ValueHash256.MemorySize];
        rand.NextBytes(randomizedBytes);
        ValueHash256 randomized = new(randomizedBytes);

        void TestForDistance(int distance)
        {
            ValueHash256 randHash = ValueHash256TestHelper.CreateRandomHashAtDistance(randomized, distance, rand);
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

    [Test]
    public void RandomHashAtDistance_RejectsInvalidDistance([Values(-1, 257)] int distance)
    {
        ValueHash256 hash = new("0x0000000000000000000000000000000000000000000000000000000000000000");

        Assert.That(() => ValueHash256TestHelper.CreateRandomHashAtDistance(hash, distance, new Random(0)), Throws.InstanceOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void TestDistanceCompare()
    {
        ValueHash256 h1 = new("0x0010000000000000000000000000000000000000000000000000000000000000");
        ValueHash256 h2 = new("0x0110000000000000000000000000000000000000000000000000000000000000");
        ValueHash256 target = h2;
        ValueHash256 bothNonZeroH2 = new("0x0012000000000000000000000000000000000000000000000000000000000000");
        ValueHash256 bothNonZeroTarget = new("0x0011000000000000000000000000000000000000000000000000000000000000");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Distance.Compare(h1, h2, target), Is.GreaterThan(0));
            Assert.That(Distance.Compare(h2, h1, target), Is.LessThan(0));
            Assert.That(Distance.Compare(h1, h1, target), Is.Zero);
            Assert.That(Distance.Compare(h1, bothNonZeroH2, bothNonZeroTarget), Is.LessThan(0));
            Assert.That(Distance.Compare(bothNonZeroH2, h1, bothNonZeroTarget), Is.GreaterThan(0));
        }
    }

    [TestCase("0x0100000000000000000000000000000000000000000000000000000000000000", "0x00000000000000ff000000000000000000000000000000000000000000000000")]
    [TestCase("0x0100000000000000000000000000000000000000000000000000000000000000", "0x0000000000000000ff00000000000000000000000000000000000000000000ff")]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000002", "0x0000000000000000000000000000000000000000000000000000000000000001")]
    public void Compare_uses_big_endian_order_across_all_words(string fartherDistance, string closerDistance)
    {
        ValueHash256 target = new("0x5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a");
        ValueHash256 farther = XorDistance(new ValueHash256(fartherDistance), target);
        ValueHash256 closer = XorDistance(new ValueHash256(closerDistance), target);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Distance.Compare(farther, closer, target), Is.GreaterThan(0));
            Assert.That(Distance.Compare(closer, farther, target), Is.LessThan(0));
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

    [Test]
    public void Distance_operations_match_byte_wise_reference_on_random_and_edge_inputs()
    {
        Random random = new(1);
        List<ValueHash256> hashes = [default, new("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")];
        Span<byte> bytes = stackalloc byte[ValueHash256.MemorySize];
        for (int index = 0; index < ValueHash256.MemorySize * 8; index++)
        {
            bytes.Clear();
            bytes[index / 8] = (byte)(1 << (7 - (index % 8)));
            hashes.Add(new ValueHash256(bytes));
            bytes.Fill(0xff);
            bytes[index / 8] = (byte)~(1 << (7 - (index % 8)));
            hashes.Add(new ValueHash256(bytes));
        }

        for (int i = 0; i < 64; i++)
        {
            random.NextBytes(bytes);
            hashes.Add(new ValueHash256(bytes));
        }

        for (int i = 0; i < 256; i++)
        {
            ValueHash256 baseHash = hashes[random.Next(hashes.Count)];
            hashes.Add(ValueHash256TestHelper.CreateRandomHashAtDistance(baseHash, random.Next(Distance.MaxDistance + 1), random));
        }

        for (int i = 0; i < hashes.Count; i++)
        {
            for (int j = 0; j < hashes.Count; j++)
            {
                int expected = ReferenceLogDistance(hashes[i], hashes[j]);
                if (Distance.CalculateLogDistance(hashes[i], hashes[j]) != expected)
                {
                    Assert.Fail($"Log distance mismatch for {hashes[i]} and {hashes[j]}, expected {expected}");
                }
            }
        }

        for (int i = 0; i < 20000; i++)
        {
            ValueHash256 left = hashes[random.Next(hashes.Count)];
            ValueHash256 right = hashes[random.Next(hashes.Count)];
            ValueHash256 target = hashes[random.Next(hashes.Count)];
            int expected = ReferenceCompareSign(left, right, target);
            if (Math.Sign(Distance.Compare(left, right, target)) != expected)
            {
                Assert.Fail($"Compare mismatch for {left}, {right} and target {target}, expected sign {expected}");
            }
        }
    }

    private static int ReferenceLogDistance(ValueHash256 left, ValueHash256 right)
    {
        ValueHash256 distance = XorDistance(left, right);
        ReadOnlySpan<byte> xor = distance.Bytes;
        int zeros = 0;
        for (int i = 0; i < xor.Length; i++)
        {
            byte value = xor[i];
            if (value == 0)
            {
                zeros += 8;
                continue;
            }

            int bitLength = 1;
            while ((value >>= 1) != 0)
            {
                bitLength++;
            }

            zeros += 8 - bitLength;
            break;
        }

        return ValueHash256.MemorySize * 8 - zeros;
    }

    private static int ReferenceCompareSign(ValueHash256 left, ValueHash256 right, ValueHash256 target)
    {
        ValueHash256 leftDistance = XorDistance(left, target);
        ValueHash256 rightDistance = XorDistance(right, target);
        return Math.Sign(leftDistance.Bytes.SequenceCompareTo(rightDistance.Bytes));
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
