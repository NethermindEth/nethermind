// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Linq;
using FluentAssertions;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.Messages;

[TestFixture, Parallelizable(ParallelScope.All)]
public class RlpByteArrayListTests
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
    public void SequentialAccess_ReturnsCorrectData(byte[][] items)
    {
        using RlpByteArrayList list = CreateList(items);
        list.Count.Should().Be(items.Length);

        for (int i = 0; i < items.Length; i++)
        {
            list[i].ToArray().Should().BeEquivalentTo(items[i], $"item {i} should match");
        }
    }

    [TestCaseSource(nameof(TestCases))]
    public void SameIndexReAccess_ReturnsSameResult(byte[][] items)
    {
        using RlpByteArrayList list = CreateList(items);

        for (int i = 0; i < items.Length; i++)
        {
            byte[] first = list[i].ToArray();
            byte[] second = list[i].ToArray();
            second.Should().BeEquivalentTo(first, $"re-access of item {i} should be identical");
        }
    }

    [TestCaseSource(nameof(TestCases))]
    public void BackwardAccess_ReturnsCorrectData(byte[][] items)
    {
        if (items.Length < 2) return;

        using RlpByteArrayList list = CreateList(items);

        // Access last item first to set cache forward
        list[items.Length - 1].ToArray().Should().BeEquivalentTo(items[^1]);
        // Then access first item (requires cache reset)
        list[0].ToArray().Should().BeEquivalentTo(items[0]);
        // Access middle if possible
        if (items.Length > 2)
        {
            int mid = items.Length / 2;
            list[mid].ToArray().Should().BeEquivalentTo(items[mid]);
        }
    }

    private static RlpByteArrayList CreateList(byte[][] items)
    {
        int contentLength = 0;
        for (int i = 0; i < items.Length; i++)
        {
            contentLength += Rlp.LengthOf(items[i]);
        }

        int totalLength = Rlp.LengthOfSequence(contentLength);
        RlpStream rlpStream = new(totalLength);

        rlpStream.StartSequence(contentLength);
        int dataStart = rlpStream.Position;
        for (int i = 0; i < items.Length; i++)
        {
            rlpStream.Encode(items[i]);
        }

        byte[] data = rlpStream.Data.ToArray()!;
        ExactMemoryOwner memoryOwner = new(data);
        return new RlpByteArrayList(memoryOwner, memoryOwner.Memory.Slice(dataStart, contentLength));
    }

    private sealed class ExactMemoryOwner(byte[] data) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory => data;
        public void Dispose() { }
    }
}
