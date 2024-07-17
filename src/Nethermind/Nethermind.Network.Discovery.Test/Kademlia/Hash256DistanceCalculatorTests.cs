// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.Intrinsics;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Kademlia;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class Hash256DistanceCalculatorTests
{
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0x0000000000000000000000000000000000000000000000000000000000000000", 0)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", 256)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0xF000000000000000000000000000000000000000000000000000000000000000", 256)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0xE000000000000000000000000000000000000000000000000000000000000000", 256)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0x7000000000000000000000000000000000000000000000000000000000000000", 255)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0x0F000000000000000000000000000000000000000000000000000000000000000", 248)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0x0E000000000000000000000000000000000000000000000000000000000000000", 248)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0x07000000000000000000000000000000000000000000000000000000000000000", 247)]
    public void TestDistance(string hash1, string hash2, int expectedDistance)
    {
        Hash256DistanceCalculator calculator = new Hash256DistanceCalculator();
        calculator.CalculateDistance(new ValueHash256(hash1), new ValueHash256(hash2)).Should().Be(expectedDistance);
        calculator.CalculateDistance(new ValueHash256(hash2), new ValueHash256(hash1)).Should().Be(expectedDistance);
    }


    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0x0000000000000000000000000000000000000000000000000000000000000000", 256)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0x0000000000000000000000000000000000000000000000000000000000000000", 255)]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0x000000000000000000000000000000000000000000000000000000000000b30d", 240)]
    // TODO: Fixthis
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0x000000000000000000000000000000000000000000000000000000000080b30d", 239)]
    public void TestCopyRandom(string hash1str, string expectedstr, int distance)
    {
        ValueHash256 hash1 = new ValueHash256(hash1str);
        ValueHash256 expectedHash = new ValueHash256(expectedstr);
        ValueHash256 randomized = new ValueHash256();
        new Random(0).NextBytes(randomized.BytesAsSpan);

        Hash256DistanceCalculator.CopyForRandom(hash1, randomized, distance).Should().Be(expectedHash);
    }

    [TestCase]
    public void TestDistanceCompare()
    {
        ValueHash256 h1 = new ValueHash256("0x0010000000000000000000000000000000000000000000000000000000000000");
        ValueHash256 h2 = new ValueHash256("0x0110000000000000000000000000000000000000000000000000000000000000");
        ValueHash256 h3 = new ValueHash256("0x0000000000000000000000000000000000000000000000000000000000000000");

        Hash256DistanceCalculator calculator = new Hash256DistanceCalculator();
        calculator.Compare(h1, h2, h3).Should().BeLessThan(0);
    }

    [TestCase]
    public void Strange()
    {
        ValueHash256 a = new ValueHash256("0x1a0c466f5d75e4d8ad6765d5f519dbc82b7c343b37f88500ec5e64005393b30d");
        ValueHash256 b = new ValueHash256("0x82bf3eb6be6c2d15511b0dc6c68c97bad52b834b11656c6104af44123e565a3d");

        Vector<byte> aBig = new Vector<byte>(a.BytesAsSpan);
        Vector<byte> bBig = new Vector<byte>(b.BytesAsSpan);

        ValueHash256 xored = new ValueHash256();
        (aBig ^ bBig).CopyTo(xored.BytesAsSpan);

        Console.Error.WriteLine($"The three {a} {b} {xored}");

        // Hash256DistanceCalculator calculator = new Hash256DistanceCalculator();
        // Console.Error.WriteLine($"Distance {calculator.CalculateDistance(a, b)} {calculator.BigIntLogDist(a, b)}");
        // Console.Error.WriteLine($"Distanceb {calculator.BigIntDist(a, b)} ");
    }
}
