// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtTrieNodeGroupTests
{
    /// <summary>The whole header: the presence and stem bitmaps, the run one, the stats and the format byte.</summary>
    private const int TrailerLength = 4 + 4 + 2 + PbtSubtreeStats.EncodedLength + 1;

    [TestCase(PbtGroupFormat.EveryLevel)]
    [TestCase(PbtGroupFormat.Interleaved)]
    public void PositionMath_EncodeDecodeRoundTrip_AndValidation(PbtGroupFormat format)
    {
        int boundaryCount = 0;
        for (int position = 0; position < PbtTrieNodeGroup.PositionCount; position++)
        {
            if (PbtTrieNodeGroup.IsBoundaryPosition(position)) boundaryCount++;
        }

        Assert.That(boundaryCount, Is.EqualTo(PbtTrieNodeGroup.BoundarySlots));
        for (int slot = 0; slot < PbtTrieNodeGroup.BoundarySlots; slot++)
        {
            int position = PbtTrieNodeGroup.BoundaryPosition(slot);
            Assert.That(PbtTrieNodeGroup.IsBoundaryPosition(position), $"slot {slot} at position {position}");
            Assert.That(PbtTrieNodeGroup.BoundarySlot(position), Is.EqualTo(slot));
        }

        // a representative mix: an inner internal at a kept level, an inner stem at a skipped one (stems
        // are stored wherever they land), a boundary internal and a boundary stem. The root position is
        // left absent — no group stores its internal root (ancestry invariants are the updater's concern,
        // not the codec's)
        Stem stemA = new(Bytes.FromHexString("0x11111111111111111111111111111111111111111111111111111111111111"));
        Stem stemB = new(Bytes.FromHexString("0x22222222222222222222222222222222222222222222222222222222222222"));
        ValueHash256 hashB = new(Bytes.FromHexString("0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        ValueHash256 hashC = new(Bytes.FromHexString("0xcccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"));
        ValueHash256 rootA = new(Bytes.FromHexString("0xdddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"));
        ValueHash256 rootB = new(Bytes.FromHexString("0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"));

        PbtTrieNodeGroup.ValueSlot[] slots = new PbtTrieNodeGroup.ValueSlot[PbtTrieNodeGroup.PositionCount];
        slots[13] = PbtTrieNodeGroup.InternalSlot(hashB);
        slots[29] = PbtTrieNodeGroup.StemSlot(stemA, rootA);
        slots[PbtTrieNodeGroup.BoundaryPosition(0)] = PbtTrieNodeGroup.InternalSlot(hashC);
        slots[PbtTrieNodeGroup.BoundaryPosition(1)] = PbtTrieNodeGroup.StemSlot(stemB, rootB);

        // the subtree holds more stems than this group's two stem nodes: the boundary internal at slot 0
        // points at a child group holding the rest
        PbtSubtreeStats stats = new(9);

        byte[] encoded = new byte[PbtTrieNodeGroup.MaxEncodedLength];
        int length = Encode(slots, stats, encoded, format);
        Assert.That(length, Is.EqualTo(TrailerLength + 4 * 32 + 2 * 31));

        PbtTrieNodeGroup decoded = PbtTrieNodeGroup.Decode(encoded.AsSpan(0, length));
        Assert.That(decoded.Stats, Is.EqualTo(stats));
        Assert.That(decoded.Format, Is.EqualTo(format));
        PbtTrieNodeGroup.ValueSlot[] roundTripped = new PbtTrieNodeGroup.ValueSlot[PbtTrieNodeGroup.PositionCount];
        for (int position = 0; position < PbtTrieNodeGroup.PositionCount; position++)
        {
            PbtTrieNodeGroup.ValueSlot slot = decoded[position].ToValue();
            Assert.That(slot.Kind, Is.EqualTo(slots[position].Kind), $"kind at {position}");
            Assert.That(slot.Stem, Is.EqualTo(slots[position].Stem), $"stem at {position}");
            Assert.That(slot.Hash, Is.EqualTo(slots[position].Hash), $"hash at {position}");
            roundTripped[position] = slot;
        }

        byte[] reencoded = new byte[PbtTrieNodeGroup.MaxEncodedLength];
        Assert.That(Encode(roundTripped, decoded.Stats, reencoded, format), Is.EqualTo(length));
        Assert.That(reencoded.AsSpan(0, length).SequenceEqual(encoded.AsSpan(0, length)));

        // an empty group encodes to nothing (the store's removal marker), whatever it is told it holds
        Assert.That(new PbtTrieNodeGroup.Builder(encoded, format).Finish(stats), Is.EqualTo(0));
        Assert.That(default(PbtTrieNodeGroup).Stats, Is.EqualTo(default(PbtSubtreeStats)), "an absent subtree holds nothing");

        // validation: an unknown format byte, position bit 31, a stem bit without its presence bit,
        // and a length that does not match the bitmaps are all rejected
        byte[] valid = encoded[..length];
        byte[] badFormat = (byte[])valid.Clone();
        badFormat[^1] = 0xFF;
        Assert.That(() => PbtTrieNodeGroup.Decode(badFormat), Throws.TypeOf<InvalidDataException>());

        // the whole header sits in the trailer: presence, then stems, then the runs, then the six-byte
        // stats, then the format byte
        int trailer = length - TrailerLength;

        byte[] highBit = (byte[])valid.Clone();
        highBit[trailer + 3] |= 0x80;
        Assert.That(() => PbtTrieNodeGroup.Decode(highBit), Throws.TypeOf<InvalidDataException>());

        byte[] orphanStem = (byte[])valid.Clone();
        orphanStem[trailer + 4] |= 0x04; // stem bit for absent position 2
        Assert.That(() => PbtTrieNodeGroup.Decode(orphanStem), Throws.TypeOf<InvalidDataException>());

        Assert.That(() => PbtTrieNodeGroup.Decode(valid.AsSpan(..^1)), Throws.TypeOf<InvalidDataException>());
        Assert.That(() => PbtTrieNodeGroup.Decode(valid.AsSpan(..4)), Throws.TypeOf<InvalidDataException>());
    }

    /// <summary>
    /// The skip mask marks exactly the odd group-relative levels, derived here from the fold's own
    /// recursion rather than restated — the two parameterisations of it must agree.
    /// </summary>
    [Test]
    public void SkippedPositions_AreExactlyTheOddLevels()
    {
        uint skipped = 0;
        uint kept = 0;
        Walk(PbtTrieNodeGroup.RootPosition, PbtTrieNodeGroup.BoundarySlots, 0);

        Assert.That(BitOperations.PopCount(skipped), Is.EqualTo(10), "levels 1 and 3 hold two and eight positions");
        Assert.That(BitOperations.PopCount(kept), Is.EqualTo(PbtTrieNodeGroup.PositionCount - 10));
        Assert.That(skipped & kept, Is.Zero, "a position is either stored or folded, never both");
        Assert.That(kept | skipped, Is.EqualTo((1u << PbtTrieNodeGroup.PositionCount) - 1), "every position is accounted for");

        // a boundary slot is a level of its own and is always stored
        for (int slot = 0; slot < PbtTrieNodeGroup.BoundarySlots; slot++)
        {
            Assert.That(
                PbtTrieNodeGroup.IsSkippedPosition(PbtGroupFormat.Interleaved, PbtTrieNodeGroup.BoundaryPosition(slot)),
                Is.False, $"boundary slot {slot}");
        }

        for (int position = 0; position < PbtTrieNodeGroup.PositionCount; position++)
        {
            Assert.That(PbtTrieNodeGroup.IsSkippedPosition(PbtGroupFormat.EveryLevel, position), Is.False, $"position {position}");
        }

        void Walk(int position, int width, int level)
        {
            bool storesInternal = PbtTrieNodeGroup.StoresInternalAtWidth(PbtGroupFormat.Interleaved, width);
            Assert.That(
                PbtTrieNodeGroup.IsSkippedPosition(PbtGroupFormat.Interleaved, position), Is.EqualTo(!storesInternal),
                $"position {position} at level {level} (width {width}): the by-position and by-width answers must agree");
            Assert.That(storesInternal, Is.EqualTo(level % 2 == 0), $"level {level} is {(level % 2 == 0 ? "kept" : "skipped")}");

            if (storesInternal) kept |= 1u << position; else skipped |= 1u << position;
            if (width == 1) return;

            Walk(position - width, width / 2, level + 1);
            Walk(position - 1, width / 2, level + 1);
        }
    }

    /// <summary>
    /// An interleaved group folds its odd levels rather than storing them, so an internal node at one
    /// is not a thing the encoding can say — while a stem there is, and must survive.
    /// </summary>
    [Test]
    public void Interleaved_RejectsAnInternalNodeAtASkippedLevel()
    {
        ValueHash256 hash = new(Bytes.FromHexString("0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        Stem stem = new(Bytes.FromHexString("0x11111111111111111111111111111111111111111111111111111111111111"));
        PbtSubtreeStats stats = new(2);

        PbtTrieNodeGroup.ValueSlot[] slots = new PbtTrieNodeGroup.ValueSlot[PbtTrieNodeGroup.PositionCount];
        slots[14] = PbtTrieNodeGroup.InternalSlot(hash); // level 1: skipped

        // the every-level format is what such an encoding can only be, and it still decodes
        byte[] encoded = new byte[PbtTrieNodeGroup.MaxEncodedLength];
        int length = Encode(slots, stats, encoded, PbtGroupFormat.EveryLevel);
        Assert.That(PbtTrieNodeGroup.Decode(encoded.AsSpan(0, length)).Format, Is.EqualTo(PbtGroupFormat.EveryLevel));

        // relabelling those same bytes interleaved is rejected: position 14 holds an internal node
        byte[] mislabelled = encoded[..length];
        mislabelled[^1] = (byte)PbtGroupFormat.Interleaved;
        Assert.That(() => PbtTrieNodeGroup.Decode(mislabelled), Throws.TypeOf<InvalidDataException>());

        // a stem at that very position is legal in both: nothing recomputes a stem
        slots[14] = PbtTrieNodeGroup.StemSlot(stem, hash);
        foreach (PbtGroupFormat format in (PbtGroupFormat[])[PbtGroupFormat.EveryLevel, PbtGroupFormat.Interleaved])
        {
            int stemLength = Encode(slots, stats, encoded, format);
            PbtTrieNodeGroup decoded = PbtTrieNodeGroup.Decode(encoded.AsSpan(0, stemLength));
            Assert.That(decoded.KindAt(14), Is.EqualTo(PbtTrieNodeGroup.NodeKind.Stem), $"{format}");
            Assert.That(decoded[14].Stem, Is.EqualTo(stem), $"{format}");
        }
    }

    /// <summary>
    /// The group root's internal node is folded and cached in the parent's boundary slot, so no group
    /// stores it — while a stem may occupy the root position, and must survive. Both hold whatever the
    /// format: the root skip is orthogonal to the format byte.
    /// </summary>
    [Test]
    public void RejectsAnInternalNodeAtTheRootPosition_ButKeepsAStem()
    {
        ValueHash256 hash = new(Bytes.FromHexString("0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        Stem stem = new(Bytes.FromHexString("0x11111111111111111111111111111111111111111111111111111111111111"));
        PbtSubtreeStats stats = new(1);

        PbtTrieNodeGroup.ValueSlot[] slots = new PbtTrieNodeGroup.ValueSlot[PbtTrieNodeGroup.PositionCount];
        byte[] encoded = new byte[PbtTrieNodeGroup.MaxEncodedLength];

        slots[PbtTrieNodeGroup.RootPosition] = PbtTrieNodeGroup.InternalSlot(hash);
        foreach (PbtGroupFormat format in (PbtGroupFormat[])[PbtGroupFormat.EveryLevel, PbtGroupFormat.Interleaved])
        {
            int length = Encode(slots, stats, encoded, format);
            Assert.That(() => PbtTrieNodeGroup.Decode(encoded.AsSpan(0, length)), Throws.TypeOf<InvalidDataException>(), $"{format}");
        }

        // a stem at the root position is legal in both and round-trips: nothing recomputes a stem
        slots[PbtTrieNodeGroup.RootPosition] = PbtTrieNodeGroup.StemSlot(stem, hash);
        foreach (PbtGroupFormat format in (PbtGroupFormat[])[PbtGroupFormat.EveryLevel, PbtGroupFormat.Interleaved])
        {
            int length = Encode(slots, stats, encoded, format);
            PbtTrieNodeGroup decoded = PbtTrieNodeGroup.Decode(encoded.AsSpan(0, length));
            Assert.That(decoded.KindAt(PbtTrieNodeGroup.RootPosition), Is.EqualTo(PbtTrieNodeGroup.NodeKind.Stem), $"{format}");
            Assert.That(decoded[PbtTrieNodeGroup.RootPosition].Stem, Is.EqualTo(stem), $"{format}");
        }
    }

    /// <summary>
    /// The software PEXT behind <see cref="PbtTrieNodeGroup.BoundaryShape"/> must gather exactly the
    /// boundary bits of a position bitmap, each landing at its own slot index — the same answer as
    /// testing the positions one at a time.
    /// </summary>
    [TestCase(0x00000000u)]
    [TestCase(0xFFFFFFFFu)]
    [TestCase(0x06CD8D9Bu)] // exactly the boundary positions, so every slot fills
    [TestCase(0xF9327264u)] // exactly the non-boundary ones, so no slot does
    [TestCase(0x12345678u)]
    [TestCase(0xDEADBEEFu)]
    [TestCase(0xAAAAAAAAu)]
    [TestCase(0x55555555u)]
    public void BoundaryBits_GathersBoundaryPositionsIntoSlotOrder(uint positions)
    {
        uint expected = 0;
        for (int position = 0; position < 32; position++)
        {
            if ((positions >> position & 1) == 0 || !PbtTrieNodeGroup.IsBoundaryPosition(position)) continue;
            expected |= 1u << PbtTrieNodeGroup.BoundarySlot(position);
        }

        Assert.That(PbtTrieNodeGroup.BoundaryBits(positions), Is.EqualTo(expected), $"0x{positions:x8}");
        Assert.That(expected >> PbtTrieNodeGroup.BoundarySlots, Is.Zero, "only the sixteen slot bits are set");
    }

    [Test]
    public void BoundaryShape_EmptyGroup_IsUnoccupied() =>
        Assert.That(default(PbtTrieNodeGroup).BoundaryShape(), Is.EqualTo(default(NodeGroupBitmasks)));

    /// <summary>
    /// A run is held whole by the boundary slot it hangs from, so its entry is longer than the hash a
    /// pointer to a child group is — which the entries after it must be found past, and which the
    /// bitmaps alone have to say. Its cached node hash still ends it, so the slot reads as a boundary
    /// internal wherever only the hash is wanted.
    /// </summary>
    [TestCase(PbtGroupFormat.EveryLevel)]
    [TestCase(PbtGroupFormat.Interleaved)]
    public void ABoundaryRun_IsHeldWholeAndShiftsWhatFollowsIt(PbtGroupFormat format)
    {
        const int chainSlot = 1;
        Stem stem = new(Bytes.FromHexString("0x11111111111111111111111111111111111111111111111111111111111111"));
        ValueHash256 stemRoot = new(Bytes.FromHexString("0xdddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"));
        ValueHash256 childRoot = new(Bytes.FromHexString("0xcccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"));
        PbtSubtreeStats stats = new(9);

        // zero past its target depth, as a group's path is
        Stem targetPath = new(Bytes.FromHexString("0x0dead000000000000000000000000000000000000000000000000000000000"));
        byte[] chain = new byte[PbtNodeChain.EncodedLength];
        PbtNodeChain.Write(chain, startDepth: 8, targetDepth: 20, targetPath, childRoot, new PbtSubtreeStats(3));

        // a stem below the run and a boundary internal above it, so the run shifts the one that follows
        // and leaves the one before it where it was
        byte[] encoded = new byte[PbtTrieNodeGroup.MaxEncodedLength];
        PbtTrieNodeGroup.Builder builder = new(encoded, format);
        builder.AppendStem(PbtTrieNodeGroup.BoundaryPosition(0), stem, stemRoot);
        builder.AppendChain(PbtTrieNodeGroup.BoundaryPosition(chainSlot), chain);
        builder.AppendInternal(PbtTrieNodeGroup.BoundaryPosition(2), childRoot);
        int length = builder.Finish(stats);

        PbtTrieNodeGroup group = PbtTrieNodeGroup.Decode(encoded.AsSpan(0, length));
        Assert.That(length, Is.EqualTo(TrailerLength + 2 * 32 + Stem.Length + PbtNodeChain.EncodedLength));

        int chainPosition = PbtTrieNodeGroup.BoundaryPosition(chainSlot);
        Assert.That(group.KindAt(chainPosition), Is.EqualTo(PbtTrieNodeGroup.NodeKind.Chain));
        Assert.That(group[chainPosition].ChainData.SequenceEqual(chain));
        Assert.That(group[chainPosition].Hash, Is.EqualTo(PbtNodeChain.NodeHashOf(chain)), "a run contributes its cached node hash");
        Assert.That(group[chainPosition].NodeHash(), Is.EqualTo(PbtNodeChain.NodeHashOf(chain)));

        // the slots around it are unmoved and unconfused
        Assert.That(group[PbtTrieNodeGroup.BoundaryPosition(0)].Stem, Is.EqualTo(stem));
        Assert.That(group.KindAt(PbtTrieNodeGroup.BoundaryPosition(2)), Is.EqualTo(PbtTrieNodeGroup.NodeKind.Internal));
        Assert.That(group[PbtTrieNodeGroup.BoundaryPosition(2)].Hash, Is.EqualTo(childRoot));

        NodeGroupBitmasks boundary = group.BoundaryShape();
        Assert.That(boundary, Is.EqualTo(new NodeGroupBitmasks(0b111u, 0b001u, 1u << chainSlot)));
        Assert.That(boundary.ChildSlots, Is.EqualTo(1u << 2), "a run roots no child blob, and neither does a stem");

        // the run bitmap must name an occupied boundary slot that holds no stem
        byte[] valid = encoded[..length];
        foreach (int slot in (int[])[0, 3])
        {
            byte[] misplaced = (byte[])valid.Clone();
            BinaryPrimitives.WriteUInt16LittleEndian(misplaced.AsSpan(length - TrailerLength + 8), (ushort)(1 << slot));
            Assert.That(() => PbtTrieNodeGroup.Decode(misplaced), Throws.TypeOf<InvalidDataException>(), $"a run claimed at slot {slot}");
        }
    }

    /// <summary>
    /// Encodes positional slots through <see cref="PbtTrieNodeGroup.Builder"/>, walking positions in
    /// the ascending order it requires — the order the updater's post-order rebuild appends in.
    /// </summary>
    private static int Encode(
        ReadOnlySpan<PbtTrieNodeGroup.ValueSlot> slots, in PbtSubtreeStats stats, Span<byte> destination, PbtGroupFormat format)
    {
        PbtTrieNodeGroup.Builder builder = new(destination, format);
        for (int position = 0; position < PbtTrieNodeGroup.PositionCount; position++)
        {
            PbtTrieNodeGroup.ValueSlot slot = slots[position];
            switch (slot.Kind)
            {
                case PbtTrieNodeGroup.NodeKind.Internal:
                    builder.AppendInternal(position, slot.Hash);
                    break;
                case PbtTrieNodeGroup.NodeKind.Stem:
                    builder.AppendStem(position, slot.Stem, slot.Hash);
                    break;
            }
        }

        return builder.Finish(stats);
    }
}
