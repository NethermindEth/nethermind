// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.Messages;

[TestFixture, Parallelizable(ParallelScope.All)]
public class RlpItemListTests
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
        using RlpItemList list = CreateList(items);
        list.Count.Should().Be(items.Length);

        for (int i = 0; i < items.Length; i++)
        {
            byte[] expectedRlp = Rlp.Encode(items[i]).Bytes;
            list[i].ToArray().Should().BeEquivalentTo(expectedRlp, $"item {i} should be raw RLP");
        }
    }

    [TestCaseSource(nameof(TestCases))]
    public void SameIndexReAccess_ReturnsSameResult(byte[][] items)
    {
        using RlpItemList list = CreateList(items);

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

        using RlpItemList list = CreateList(items);

        byte[] lastExpected = Rlp.Encode(items[^1]).Bytes;
        list[items.Length - 1].ToArray().Should().BeEquivalentTo(lastExpected);

        byte[] firstExpected = Rlp.Encode(items[0]).Bytes;
        list[0].ToArray().Should().BeEquivalentTo(firstExpected);

        if (items.Length > 2)
        {
            int mid = items.Length / 2;
            byte[] midExpected = Rlp.Encode(items[mid]).Bytes;
            list[mid].ToArray().Should().BeEquivalentTo(midExpected);
        }
    }

    [Test]
    public void PooledChild_SequentialDispose_ReleasesAllLeases()
    {
        // Parent with 3 nested lists, each containing one byte item.
        // Sequential pattern: get child, use, dispose, repeat.
        TrackingMemoryOwner owner = CreateNestedList(out RlpItemList parent,
            new byte[][][] { [[0x01]], [[0x02]], [[0x03]] });

        for (int i = 0; i < 3; i++)
        {
            using IRlpItemList child = parent.GetNestedItemList(i);
            child.Count.Should().Be(1);
            child.ReadContent(0).ToArray().Should().BeEquivalentTo(new[] { (byte)(i + 1) });
        }

        parent.Dispose();
        owner.DisposeCount.Should().Be(1, "inner owner should be disposed exactly once");
    }

    [Test]
    public void PooledChild_TwoChildrenAliveSimultaneously_ReleasesAllLeases()
    {
        TrackingMemoryOwner owner = CreateNestedList(out RlpItemList parent,
            new byte[][][] { [[0x01]], [[0x02]] });

        IRlpItemList child0 = parent.GetNestedItemList(0);
        IRlpItemList child1 = parent.GetNestedItemList(1);

        child0.ReadContent(0).ToArray().Should().BeEquivalentTo(new byte[] { 0x01 });
        child1.ReadContent(0).ToArray().Should().BeEquivalentTo(new byte[] { 0x02 });

        child0.Dispose();
        child1.Dispose();
        parent.Dispose();
        owner.DisposeCount.Should().Be(1);
    }

    [Test]
    public void PooledChild_ParentDisposedBeforeChild_ReleasesAllLeases()
    {
        TrackingMemoryOwner owner = CreateNestedList(out RlpItemList parent,
            new byte[][][] { [[0xAA]] });

        IRlpItemList child = parent.GetNestedItemList(0);
        child.ReadContent(0).ToArray().Should().BeEquivalentTo(new byte[] { 0xAA });

        parent.Dispose();
        owner.DisposeCount.Should().Be(0, "child still holds a lease");

        child.Dispose();
        owner.DisposeCount.Should().Be(1, "all leases released");
    }

    [Test]
    public void PooledChild_DoubleDispose_IsIdempotent()
    {
        TrackingMemoryOwner owner = CreateNestedList(out RlpItemList parent,
            new byte[][][] { [[0x01]] });

        IRlpItemList child = parent.GetNestedItemList(0);
        child.Dispose();
        child.Dispose(); // should be no-op

        parent.Dispose();
        owner.DisposeCount.Should().Be(1);
    }

    [Test]
    public void PooledChild_ReusedChildReadsCorrectData()
    {
        // Verify the pooled child is correctly reset to point at the new nested region.
        CreateNestedList(out RlpItemList parent,
            new byte[][][] { [[0x11], [0x22]], [[0x33], [0x44]] });

        using (IRlpItemList child0 = parent.GetNestedItemList(0))
        {
            child0.Count.Should().Be(2);
            child0.ReadContent(0).ToArray().Should().BeEquivalentTo(new byte[] { 0x11 });
            child0.ReadContent(1).ToArray().Should().BeEquivalentTo(new byte[] { 0x22 });
        }

        using (IRlpItemList child1 = parent.GetNestedItemList(1))
        {
            child1.Count.Should().Be(2);
            child1.ReadContent(0).ToArray().Should().BeEquivalentTo(new byte[] { 0x33 });
            child1.ReadContent(1).ToArray().Should().BeEquivalentTo(new byte[] { 0x44 });
        }

        parent.Dispose();
    }

    private static RlpItemList CreateList(byte[][] items)
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

        byte[] data = rlpStream.Data.ToArray()!;
        ExactMemoryOwner memoryOwner = new(data);
        return new RlpItemList(memoryOwner, memoryOwner.Memory.Slice(0, totalLength));
    }

    /// <summary>
    /// Creates a parent list containing nested sub-lists. Each element in <paramref name="nestedItems"/>
    /// becomes a nested RLP list of byte-string items.
    /// </summary>
    private static TrackingMemoryOwner CreateNestedList(out RlpItemList parent, byte[][][] nestedItems)
    {
        // Compute inner list lengths first
        int outerContentLength = 0;
        for (int i = 0; i < nestedItems.Length; i++)
        {
            int innerContentLength = 0;
            for (int j = 0; j < nestedItems[i].Length; j++)
            {
                innerContentLength += Rlp.LengthOf(nestedItems[i][j]);
            }
            outerContentLength += Rlp.LengthOfSequence(innerContentLength);
        }

        int totalLength = Rlp.LengthOfSequence(outerContentLength);
        RlpStream stream = new(totalLength);

        stream.StartSequence(outerContentLength);
        for (int i = 0; i < nestedItems.Length; i++)
        {
            int innerContentLength = 0;
            for (int j = 0; j < nestedItems[i].Length; j++)
            {
                innerContentLength += Rlp.LengthOf(nestedItems[i][j]);
            }
            stream.StartSequence(innerContentLength);
            for (int j = 0; j < nestedItems[i].Length; j++)
            {
                stream.Encode(nestedItems[i][j]);
            }
        }

        byte[] data = stream.Data.ToArray()!;
        TrackingMemoryOwner owner = new(data);
        parent = new RlpItemList(owner, owner.Memory.Slice(0, totalLength));
        return owner;
    }

    private sealed class ExactMemoryOwner(byte[] data) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory => data;
        public void Dispose() { }
    }

    private sealed class TrackingMemoryOwner(byte[] data) : IMemoryOwner<byte>
    {
        public int DisposeCount;
        public Memory<byte> Memory => data;
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }
}
