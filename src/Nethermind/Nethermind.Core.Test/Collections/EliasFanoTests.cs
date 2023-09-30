// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Collections.EliasFano;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.EliasFano;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

public class EliasFanoTests
{
    private readonly ulong[] _efCase0 = { 1, 3, 3, 7 };
    private readonly ulong[] _efCase1 = { 1, 3, 3, 7, 10, 25, 98, 205, 206, 207, 807, 850, 899, 999 };
    private readonly ulong[] _efCase2 = { 1, 3, 3, 6, 7, 10 };

    [Test]
    public void TestBuilder()
    {
        UIntPtr[] data = new UIntPtr[200];
        for (uint i = 0; i < 200; i++) data[i] = i * 20000;

        EliasFanoBuilder efb = new(data[^1], data.Length);

        foreach (UIntPtr val in data) efb.Push(val);
        EliasFano ef = efb.Build();

        ef.Rank(300000).Should().Be(15);
    }

    [Test]
    public void TestEncoding()
    {
        EliasFanoBuilder efb = new(1000, 14);
        efb.Extend(_efCase1);

        EliasFano ef = efb.Build();

        AssertEfForCase1(ef);

        EliasFanoDecoder decoder = new();

        RlpStream stream = new(decoder.GetLength(ef, RlpBehaviors.None));
        decoder.Encode(stream, ef);

        EliasFano efDecoded = decoder.Decode(new RlpStream(stream.Data!));
        AssertEfForCase1(efDecoded);
    }

    [Test]
    public void TestCase()
    {
        EliasFanoBuilder efb = new(8, 4);
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
        EliasFanoBuilder efb = new(1000, 14);
        efb.Extend(_efCase1);

        EliasFano ef = efb.Build();
        AssertEfForCase1(ef);
    }

    [Test]
    public void TestIteration()
    {
        EliasFanoBuilder efb = new(1000, 14);
        efb.Extend(_efCase1);
        EliasFano ef = efb.Build();
        ef.GetEnumerator(0).ToArray().Should().BeEquivalentTo(_efCase1);
    }


    private static void AssertEfForCase1(EliasFano ef)
    {
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

    [Test]
    public void TestDelta()
    {
        EliasFanoBuilder efb = new(8, 4);
        efb.Extend(_efCase0);

        EliasFano ef = efb.Build();
        ef.Delta(0).Should().Be(1);
        ef.Delta(1).Should().Be(2);
        ef.Delta(2).Should().Be(0);
        ef.Delta(3).Should().Be(4);
        ef.Delta(4).Should().BeNull();
    }

    [Test]
    public void TestBinSearch()
    {
        EliasFanoBuilder efb = new(11, 6);
        efb.Extend(_efCase2);

        EliasFano ef = efb.Build();

        ef.BinSearchRange(0, ef.Length, 6).Should().Be(3);
        ef.BinSearchRange(0, ef.Length, 10).Should().Be(5);
        ef.BinSearchRange(0, ef.Length, 9).Should().BeNull();
    }

    [Test]
    public void TestBinSearchRange()
    {
        EliasFanoBuilder efb = new(11, 6);
        efb.Extend(_efCase2);

        EliasFano ef = efb.Build();

        ef.BinSearchRange(1, 4, 6).Should().Be(3);
        ef.BinSearchRange(5, 6, 10).Should().Be(5);
        ef.BinSearchRange(1, 3, 9).Should().BeNull();
    }

    [Test]
    public void TestSelect()
    {
        EliasFanoBuilder efb = new(8, 4);
        efb.Extend(_efCase0);

        EliasFano ef = efb.Build();
        ef.Select(0).Should().Be(1);
        ef.Select(1).Should().Be(3);
        ef.Select(2).Should().Be(3);
        ef.Select(3).Should().Be(7);
        ef.Select(4).Should().BeNull();
    }

    [Test]
    public void TestPredecessor()
    {
        EliasFanoBuilder efb = new(8, 4);
        efb.Extend(_efCase0);

        EliasFano ef = efb.Build();
        ef.Predecessor(4).Should().Be(3);
        ef.Predecessor(3).Should().Be(3);
        ef.Predecessor(2).Should().Be(1);
        ef.Predecessor(0).Should().BeNull();
    }

    [Test]
    public void TestSuccessor()
    {
        EliasFanoBuilder efb = new(8, 4);
        efb.Extend(_efCase0);

        EliasFano ef = efb.Build();
        ef.Successor(0).Should().Be(1);
        ef.Successor(2).Should().Be(3);
        ef.Successor(3).Should().Be(3);
        ef.Successor(8).Should().BeNull();
    }
}
