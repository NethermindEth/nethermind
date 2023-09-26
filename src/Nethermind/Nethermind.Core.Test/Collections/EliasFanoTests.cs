// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Collections.EliasFano;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

public class EliasFanoTests
{
    [Test]
    public void TestBuilder()
    {
        UIntPtr[] data = new UIntPtr[200];
        for (uint i = 0; i < 200; i++) data[i] = i * 20000;

        EliasFanoBuilder efb = new (data[^1], data.Length);

        foreach (UIntPtr val in data) efb.Push(val);
        EliasFano ef = efb.Build();
        Console.WriteLine(string.Join(", ", data));
        ef.Rank(300000).Should().Be(15);
    }

    [Test]
    public void TestCase()
    {
        EliasFanoBuilder efb = new (8, 4);
        efb.Push(1);
        efb.Push(3);
        efb.Push(3);
        efb.Push(7);

        EliasFano ef = efb.Build();
        ef.Rank(3).Should().Be(1);
        ef.Rank(4).Should().Be(3);
        ef.Rank(8).Should().Be(4);
        Assert.Throws<ArgumentException>(() => ef.Rank(9));
    }

    [Test]
    public void TestCaseBlocks()
    {
        EliasFanoBuilder efb = new (1000, 14);
        efb.Push(1);
        efb.Push(3);
        efb.Push(3);
        efb.Push(7);
        efb.Push(10);
        efb.Push(25);
        efb.Push(98);
        efb.Push(205);
        efb.Push(206);
        efb.Push(207);
        efb.Push(807);
        efb.Push(850);
        efb.Push(899);
        efb.Push(999);

        EliasFano ef = efb.Build();
        ef.Rank(0).Should().Be(0);
        ef.Rank(1).Should().Be(0);
        ef.Rank(2).Should().Be(1);
        ef.Rank(3).Should().Be(1);
        ef.Rank(4).Should().Be(3);
        ef.Rank(5).Should().Be(3);
        ef.Rank(6).Should().Be(3);
        ef.Rank(7).Should().Be(3);
        ef.Rank(8).Should().Be(4);
        ef.Rank(9).Should().Be(4);
        ef.Rank(190).Should().Be(7);
        ef.Rank(200).Should().Be(7);
        ef.Rank(500).Should().Be(10);
        ef.Rank(600).Should().Be(10);
        ef.Rank(700).Should().Be(10);
        ef.Rank(750).Should().Be(10);
        ef.Rank(800).Should().Be(10);
        ef.Rank(900).Should().Be(13);
        ef.Rank(901).Should().Be(13);
        ef.Rank(902).Should().Be(13);
        ef.Rank(903).Should().Be(13);
        ef.Rank(904).Should().Be(13);
        ef.Rank(905).Should().Be(13);
        ef.Rank(1000).Should().Be(14);
        Assert.Throws<ArgumentException>(() => ef.Rank(1001));
    }
}
