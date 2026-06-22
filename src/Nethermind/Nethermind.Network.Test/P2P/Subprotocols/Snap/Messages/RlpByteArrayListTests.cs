// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Linq;
using DotNetty.Buffers;
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
        Assert.That(list.Count, Is.EqualTo(items.Length));

        for (int i = 0; i < items.Length; i++)
        {
            Assert.That(list[i].ToArray(), Is.EqualTo(items[i]), $"item {i} should match");
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
            Assert.That(second, Is.EqualTo(first), $"re-access of item {i} should be identical");
        }
    }

    [TestCaseSource(nameof(TestCases))]
    public void RlpWriter_WriteByteArrayList_WithWrapper_MatchesCanonicalEncoding(byte[][] items)
    {
        using RlpByteArrayList list = CreateList(items);
        byte[] expected = EncodeItems(items);

        byte[] buffer = new byte[list.RlpLength];
        RlpWriter writer = new(buffer);
        writer.WriteByteArrayList(list);

        Assert.That(writer.Position, Is.EqualTo(expected.Length));
        Assert.That(buffer.AsSpan(0, writer.Position).ToArray(), Is.EqualTo(expected));
    }

    [TestCaseSource(nameof(TestCases))]
    public void ByteBuffer_RlpWriter_WithWrapper_MatchesCanonicalEncoding(byte[][] items)
    {
        using RlpByteArrayList list = CreateList(items);
        byte[] expected = EncodeItems(items);

        using DisposableByteBuffer byteBuffer = Unpooled.Buffer(expected.Length).AsDisposable();
        ByteBufferRlpWriter writer = new(byteBuffer);
        writer.WriteByteArrayList(list);

        Assert.That(byteBuffer.ReadableBytes, Is.EqualTo(expected.Length));
        Assert.That(byteBuffer.AsSpan().ToArray(), Is.EqualTo(expected));
    }

    [TestCaseSource(nameof(TestCases))]
    public void BackwardAccess_ReturnsCorrectData(byte[][] items)
    {
        if (items.Length < 2) return;

        using RlpByteArrayList list = CreateList(items);

        // Access last item first to set cache forward
        Assert.That(list[items.Length - 1].ToArray(), Is.EqualTo(items[^1]));
        // Then access first item (requires cache reset)
        Assert.That(list[0].ToArray(), Is.EqualTo(items[0]));
        // Access middle if possible
        if (items.Length > 2)
        {
            int mid = items.Length / 2;
            Assert.That(list[mid].ToArray(), Is.EqualTo(items[mid]));
        }
    }

    [TestCase(0, 256, false)]
    [TestCase(1, 256, false)]
    [TestCase(255, 256, false)]
    [TestCase(256, 256, false)]
    [TestCase(257, 256, true)]
    [TestCase(1024, 256, true)]
    public void DecodeList_WithLimit_EnforcesCount(int itemCount, int limit, bool shouldThrow)
    {
        byte[] encoded = EncodeSingleByteItemList(itemCount);
        RlpLimit rlpLimit = RlpLimit.For<RlpByteArrayListTests>(limit, "test");

        if (shouldThrow)
        {
            Assert.Throws<RlpLimitException>(() =>
            {
                RlpReader ctx = new(encoded);
                using RlpByteArrayList _ = RlpByteArrayList.DecodeList(ref ctx, new ExactMemoryOwner(encoded), rlpLimit);
            });
        }
        else
        {
            RlpReader ctx = new(encoded);
            using RlpByteArrayList list = RlpByteArrayList.DecodeList(ref ctx, new ExactMemoryOwner(encoded), rlpLimit);
            Assert.That(list.Count, Is.EqualTo(itemCount));
        }
    }

    [Test]
    public void DecodeList_WithoutLimit_AcceptsLargeList()
    {
        const int count = 10_000;
        byte[] encoded = EncodeSingleByteItemList(count);

        RlpReader ctx = new(encoded);
        using RlpByteArrayList list = RlpByteArrayList.DecodeList(ref ctx, new ExactMemoryOwner(encoded));
        Assert.That(list.Count, Is.EqualTo(count));
    }

    private static byte[] EncodeSingleByteItemList(int count)
    {
        int contentLength = count * Rlp.LengthOf(new byte[] { 0x42 });
        int totalLength = Rlp.LengthOfSequence(contentLength);
        byte[] encoded = new byte[totalLength];
        RlpWriter writer = new(encoded);
        writer.StartSequence(contentLength);
        for (int i = 0; i < count; i++)
        {
            writer.Encode(new byte[] { 0x42 });
        }
        return encoded;
    }

    private static RlpByteArrayList CreateList(byte[][] items)
    {
        byte[] data = EncodeItems(items);
        ExactMemoryOwner memoryOwner = new(data);
        return new RlpByteArrayList(memoryOwner, memoryOwner.Memory.Slice(0, data.Length));
    }

    private static byte[] EncodeItems(byte[][] items)
    {
        int contentLength = 0;
        for (int i = 0; i < items.Length; i++)
        {
            contentLength += Rlp.LengthOf(items[i]);
        }

        int totalLength = Rlp.LengthOfSequence(contentLength);
        byte[] encoded = new byte[totalLength];
        RlpWriter writer = new(encoded);

        writer.StartSequence(contentLength);
        for (int i = 0; i < items.Length; i++)
        {
            writer.Encode(items[i]);
        }

        return encoded;
    }

    private sealed class ExactMemoryOwner(byte[] data) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory => data;
        public void Dispose() { }
    }
}
