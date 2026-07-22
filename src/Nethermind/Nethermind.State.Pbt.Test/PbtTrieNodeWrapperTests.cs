// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtTrieNodeWrapperTests
{
    /// <summary>The slots the wrapped group points its children at — the first and the last, so the offsets are exercised at both ends.</summary>
    private static readonly int[] ChildSlots = [0, 15];

    /// <summary>A slot between them holding a run, which the group keeps to itself.</summary>
    private const int ChainSlot = 3;

    private static readonly Stem StemPath = new(Bytes.FromHexString("0x0dead000000000000000000000000000000000000000000000000000000000"));
    private static readonly ValueHash256 ChildHash = new(Bytes.FromHexString("0xcccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"));
    private static readonly ValueHash256 StemHash = new(Bytes.FromHexString("0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
    private static readonly PbtSubtreeStats Stats = new(7);

    /// <summary>The group length and the format byte closing an encoding.</summary>
    private const int TrailerLength = 3;

    /// <summary>Depth 0 never wraps, so the zone roots below it keep the keys their columns are routed by.</summary>
    [TestCase(0, false)]
    [TestCase(4, true)]
    [TestCase(8, false)]
    [TestCase(12, true)]
    [TestCase(PbtLayout.TrieNodeGroupMaxGroupDepth, true)]
    public void IsWrappingDepth_AlternatesByGroup(int depth, bool wraps) =>
        Assert.That(PbtLayout.IsWrappingDepth(depth), Is.EqualTo(wraps));

    [Test]
    public void EncodeDecodeRoundTrip_AndDiscriminatesFromWhatItHolds()
    {
        byte[] firstChild = EncodeGroup();
        byte[] secondChild = EncodeSmallerGroup();
        byte[] encoded = Encode(firstChild, secondChild);

        // the wrapper is told from a bare group by its trailing byte alone, so a store column holds
        // both under the same kind of key
        Assert.That(PbtTrieNodeWrapper.IsWrapper(encoded));
        Assert.That(PbtTrieNodeWrapper.IsWrapper(firstChild), Is.False);
        Assert.That(PbtTrieNodeWrapper.IsWrapper([]), Is.False, "an absent blob is neither");
        Assert.That(() => PbtTrieNodeGroup.Decode(encoded), Throws.TypeOf<InvalidDataException>());

        PbtTrieNodeWrapper wrapper = PbtTrieNodeWrapper.Decode(encoded, out PbtTrieNodeGroup group);
        Assert.That(wrapper.IsEmpty, Is.False);
        Assert.That(group.Stats, Is.EqualTo(Stats));
        Assert.That(encoded[wrapper.Child(ChildSlots[0], group)], Is.EqualTo(firstChild));
        Assert.That(encoded[wrapper.Child(ChildSlots[1], group)], Is.EqualTo(secondChild));

        // The group is found from the trailing length alone, and everything else from the group: its
        // bitmaps say how many children there are, which puts the offsets right before it and the
        // children right before those. Nothing inside the group had to be rewritten to hold it here.
        byte[] bare = EncodeGroupOnly();
        Assert.That(encoded[wrapper.Group], Is.EqualTo(bare));
        Assert.That(wrapper.GroupOffset, Is.EqualTo(firstChild.Length + secondChild.Length + ChildSlots.Length * sizeof(ushort)));
        Assert.That(encoded, Has.Length.EqualTo(wrapper.GroupOffset + bare.Length + TrailerLength));

        // an unoccupied slot, a stem slot and a run slot root no blob of their own
        Assert.That(encoded[wrapper.Child(1, group)], Is.Empty);
        Assert.That(encoded[wrapper.Child(2, group)], Is.Empty, "a boundary stem's subtree lives in its leaf blob, not a child group");
        Assert.That(encoded[wrapper.Child(ChainSlot, group)], Is.Empty, "and a run's lives in the group's own encoding");
    }

    /// <summary>A bare group blob wraps nothing: every child of it is stored under a key of its own.</summary>
    [Test]
    public void Decode_ReadsABareGroupAsWrappingNothing()
    {
        byte[] bare = EncodeGroupOnly();
        PbtTrieNodeWrapper wrapper = PbtTrieNodeWrapper.Decode(bare, out PbtTrieNodeGroup group);

        Assert.That(wrapper.IsEmpty);
        Assert.That(wrapper.GroupOffset, Is.Zero);
        Assert.That(bare[wrapper.Group], Is.EqualTo(bare));
        Assert.That(group.Stats, Is.EqualTo(Stats));
        for (int slot = 0; slot < PbtLayout.TrieNodeGroupBoundarySlots; slot++) Assert.That(bare[wrapper.Child(slot, group)], Is.Empty, $"slot {slot}");
    }

    private static readonly object[] Rejections =
    [
        new object[] { "a truncated blob", Truncate(TrailerLength) },
        new object[] { "a group longer than the blob", GroupLength(ushort.MaxValue) },
        new object[] { "a group of no bytes", GroupLength(0) },
        new object[] { "a child ending past the offsets", Offset(0, ushort.MaxValue) },
        new object[] { "a child ending where the previous one did", Offset(1, EncodeGroup().Length) },
        new object[] { "an empty first child", Offset(0, 0) },
        new object[] { "children falling short of the offsets", Offset(1, EncodeGroup().Length + 1) },
    ];

    [TestCaseSource(nameof(Rejections))]
    public void Decode_Rejects(string description, Func<byte[], byte[]> corrupt)
    {
        byte[] corrupted = corrupt(Encode(EncodeGroup(), EncodeChain()));
        Assert.That(() => PbtTrieNodeWrapper.Decode(corrupted, out _), Throws.TypeOf<InvalidDataException>());
    }

    /// <summary>
    /// Nothing records how many children a wrapper holds — the group it wraps pins that, a blob per
    /// boundary internal — so one built for any other number has its offsets in the wrong place and is
    /// rejected however well-formed each child is.
    /// </summary>
    [Test]
    public void Decode_RejectsAChildCountItsGroupDoesNotPinDown()
    {
        // the group points at two children, and only the first of them is here
        byte[] child = EncodeGroup();
        byte[] group = EncodeGroupOnly();
        byte[] encoded = new byte[PbtTrieNodeWrapper.EncodedLength(child.Length, group.Length, 1)];
        BufferWriter writer = new(encoded);
        PbtTrieNodeWrapper.Builder builder = default;
        builder.AppendChild(ref writer, child);
        builder.WriteOffsets(ref writer);
        writer.Write(group);
        builder.Finish(ref writer, group.Length);

        Assert.That(() => PbtTrieNodeWrapper.Decode(encoded, out _), Throws.TypeOf<InvalidDataException>());
    }

    private static Func<byte[], byte[]> Truncate(int length) => blob => blob[^length..];

    private static Func<byte[], byte[]> GroupLength(int length) => blob =>
    {
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(blob.Length - TrailerLength), (ushort)length);
        return blob;
    };

    /// <summary>Rewrites child <paramref name="index"/>'s end offset, which sits with its fellows just ahead of the group.</summary>
    private static Func<byte[], byte[]> Offset(int index, int end) => blob =>
    {
        int tableOffset = blob.Length - TrailerLength - EncodeGroupOnly().Length - ChildSlots.Length * sizeof(ushort);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(tableOffset + index * sizeof(ushort)), (ushort)end);
        return blob;
    };

    private static byte[] Encode(params byte[][] children)
    {
        int childBytes = 0;
        foreach (byte[] child in children) childBytes += child.Length;

        byte[] group = EncodeGroupOnly();
        byte[] encoded = new byte[PbtTrieNodeWrapper.EncodedLength(childBytes, group.Length, children.Length)];
        BufferWriter writer = new(encoded);
        PbtTrieNodeWrapper.Builder builder = default;
        foreach (byte[] child in children) builder.AppendChild(ref writer, child);
        builder.WriteOffsets(ref writer);
        writer.Write(group);
        builder.Finish(ref writer, group.Length);
        Assert.That(writer.WrittenCount, Is.EqualTo(encoded.Length));
        return encoded;
    }

    /// <summary>
    /// The wrapping group: a boundary internal per <see cref="ChildSlots"/> entry, and a stem and a run
    /// that root no blob.
    /// </summary>
    private static byte[] EncodeGroupOnly()
    {
        byte[] encoded = new byte[PbtTrieNodeGroup.MaxEncodedLength];
        PbtGroupEncoder builder = new(encoded, PbtGroupFormat.Interleaved);
        builder.AppendInternal(PbtLayout.TrieNodeGroupBoundarySlotPosition(ChildSlots[0]), ChildHash);
        builder.AppendStem(PbtLayout.TrieNodeGroupBoundarySlotPosition(2), StemPath, StemHash);
        builder.AppendChain(PbtLayout.TrieNodeGroupBoundarySlotPosition(ChainSlot), EncodeChain());
        builder.AppendInternal(PbtLayout.TrieNodeGroupBoundarySlotPosition(ChildSlots[1]), ChildHash);
        return encoded[..builder.Finish(Stats)];
    }

    private static byte[] EncodeGroup()
    {
        byte[] encoded = new byte[PbtTrieNodeGroup.MaxEncodedLength];
        PbtGroupEncoder builder = new(encoded, PbtGroupFormat.EveryLevel);
        builder.AppendStem(PbtLayout.TrieNodeGroupBoundarySlotPosition(0), StemPath, StemHash);
        builder.AppendInternal(PbtLayout.TrieNodeGroupBoundarySlotPosition(1), ChildHash);
        return encoded[..builder.Finish(Stats)];
    }

    /// <summary>A second child, shorter than <see cref="EncodeGroup"/>'s, so that the offsets must be read rather than guessed.</summary>
    private static byte[] EncodeSmallerGroup()
    {
        byte[] encoded = new byte[PbtTrieNodeGroup.MaxEncodedLength];
        PbtGroupEncoder builder = new(encoded, PbtGroupFormat.EveryLevel);
        builder.AppendInternal(PbtLayout.TrieNodeGroupBoundarySlotPosition(0), ChildHash);
        return encoded[..builder.Finish(Stats)];
    }

    private static byte[] EncodeChain()
    {
        byte[] encoded = new byte[PbtNodeChain.EncodedLength];
        PbtNodeChain.Write(encoded, 8, 20, StemPath, ChildHash, Stats);
        return encoded;
    }
}
