// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.Messages;

[TestFixture, Parallelizable(ParallelScope.All)]
public class RlpListReaderTests
{
    private static byte[][] SingleByteItems => [
        [0x01], [0x42], [0x7f]
    ];

    private static byte[][] ShortStringItems => [
        [0xde, 0xad],
        [0xfe, 0xed, 0xca, 0xfe],
        [0x01, 0x02, 0x03]
    ];

    private static byte[][] LargeItems => [
        Enumerable.Range(0, 56).Select(i => (byte)i).ToArray(),
        Enumerable.Range(0, 100).Select(i => (byte)(i ^ 0xAA)).ToArray()
    ];

    private static byte[][] MixedItems => [
        [],
        [0x42],
        [0xde, 0xad, 0xc0, 0xde],
        [],
        Enumerable.Range(0, 60).Select(i => (byte)i).ToArray(),
        [0x01]
    ];

    private static readonly TestCaseData[] TestCases =
    [
        new TestCaseData((object)SingleByteItems).SetName("SingleByteItems"),
        new TestCaseData((object)ShortStringItems).SetName("ShortStringItems"),
        new TestCaseData((object)LargeItems).SetName("LargeItems"),
        new TestCaseData((object)new byte[][] { [], [], [] }).SetName("EmptyItems"),
        new TestCaseData((object)MixedItems).SetName("MixedItems"),
        new TestCaseData((object)new byte[][] { [0xde, 0xad] }).SetName("SingleItem"),
    ];

    [TestCaseSource(nameof(TestCases))]
    public void SequentialAccess_ReturnsRawRlpItems(byte[][] items)
    {
        byte[] data = EncodeList(items);
        RlpListReader reader = new(data);

        reader.Count.Should().Be(items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            byte[] expectedRlp = Rlp.Encode(items[i]).Bytes;
            reader[i].ToArray().Should().BeEquivalentTo(expectedRlp, $"item {i} should be raw RLP");
        }
    }

    [TestCaseSource(nameof(TestCases))]
    public void SameIndexReAccess_ReturnsSameResult(byte[][] items)
    {
        byte[] data = EncodeList(items);
        RlpListReader reader = new(data);

        for (int i = 0; i < items.Length; i++)
        {
            byte[] first = reader[i].ToArray();
            byte[] second = reader[i].ToArray();
            second.Should().BeEquivalentTo(first, $"re-access of item {i} should be identical");
        }
    }

    [TestCaseSource(nameof(TestCases))]
    public void BackwardAccess_ReturnsCorrectData(byte[][] items)
    {
        if (items.Length < 2) return;

        byte[] data = EncodeList(items);
        RlpListReader reader = new(data);

        byte[] lastExpected = Rlp.Encode(items[^1]).Bytes;
        reader[items.Length - 1].ToArray().Should().BeEquivalentTo(lastExpected);

        byte[] firstExpected = Rlp.Encode(items[0]).Bytes;
        reader[0].ToArray().Should().BeEquivalentTo(firstExpected);

        if (items.Length > 2)
        {
            int mid = items.Length / 2;
            byte[] midExpected = Rlp.Encode(items[mid]).Bytes;
            reader[mid].ToArray().Should().BeEquivalentTo(midExpected);
        }
    }

    [TestCaseSource(nameof(TestCases))]
    public void LazyCount_MatchesExpected(byte[][] items)
    {
        byte[] data = EncodeList(items);
        RlpListReader reader = new(data);
        reader.Count.Should().Be(items.Length);
        reader.Count.Should().Be(items.Length, "second access should return same count");
    }

    [Test]
    public void RecursiveUse_ListOfLists()
    {
        byte[][] inner1 = [[0x01], [0x02]];
        byte[][] inner2 = [[0xaa, 0xbb], [0xcc]];

        byte[] rlpInner1 = EncodeList(inner1);
        byte[] rlpInner2 = EncodeList(inner2);

        // Encode outer list containing two inner lists as raw bytes
        int contentLength = rlpInner1.Length + rlpInner2.Length;
        int totalLength = Rlp.LengthOfSequence(contentLength);
        RlpStream outerStream = new(totalLength);
        outerStream.StartSequence(contentLength);
        outerStream.Write(rlpInner1);
        outerStream.Write(rlpInner2);
        byte[] outerData = outerStream.Data.ToArray()!;

        RlpListReader outerReader = new(outerData);
        outerReader.Count.Should().Be(2);

        // Parse first sub-list
        ReadOnlySpan<byte> subItem0 = outerReader[0];
        RlpListReader subReader0 = new(subItem0);
        subReader0.Count.Should().Be(2);
        subReader0[0].ToArray().Should().BeEquivalentTo(Rlp.Encode(inner1[0]).Bytes);
        subReader0[1].ToArray().Should().BeEquivalentTo(Rlp.Encode(inner1[1]).Bytes);

        // Parse second sub-list
        ReadOnlySpan<byte> subItem1 = outerReader[1];
        RlpListReader subReader1 = new(subItem1);
        subReader1.Count.Should().Be(2);
        subReader1[0].ToArray().Should().BeEquivalentTo(Rlp.Encode(inner2[0]).Bytes);
        subReader1[1].ToArray().Should().BeEquivalentTo(Rlp.Encode(inner2[1]).Bytes);
    }

    private static byte[] EncodeList(byte[][] items)
    {
        int contentLength = 0;
        for (int i = 0; i < items.Length; i++)
        {
            contentLength += Rlp.LengthOf(items[i]);
        }

        int totalLength = Rlp.LengthOfSequence(contentLength);
        RlpStream rlpStream = new(totalLength);
        rlpStream.StartSequence(contentLength);
        for (int i = 0; i < items.Length; i++)
        {
            rlpStream.Encode(items[i]);
        }

        return rlpStream.Data.ToArray()!;
    }
}
