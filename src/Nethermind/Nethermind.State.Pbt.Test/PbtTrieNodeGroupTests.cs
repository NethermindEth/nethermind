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

using Layout = Nethermind.Pbt.PbtClusteredTileLayout;

namespace Nethermind.State.Pbt.Test;

public class PbtTrieNodeGroupTests
{
    /// <summary>The whole header: the presence and stem bitmaps, the run one, the stats and the format byte.</summary>
    private const int TrailerLength = 4 + 4 + 2 + PbtSubtreeStats.EncodedLength + 1;

    /// <summary>Every encoding a group may be in; the root and stem rules hold across all of them.</summary>
    private static readonly PbtGroupFormat[] Formats =
        [PbtGroupFormat.EveryLevel, PbtGroupFormat.Interleaved, PbtGroupFormat.BoundaryOnly];

    [TestCase(PbtGroupFormat.EveryLevel)]
    [TestCase(PbtGroupFormat.Interleaved)]
    public void PositionMath_EncodeDecodeRoundTrip_AndValidation(PbtGroupFormat format)
    {
        int boundaryCount = 0;
        for (int position = 0; position < Layout.PositionCount; position++)
        {
            if (PbtLayout.TrieNodeGroupIsBoundaryPosition(position)) boundaryCount++;
        }

        Assert.That(boundaryCount, Is.EqualTo(Layout.BoundarySlots));
        for (int slot = 0; slot < Layout.BoundarySlots; slot++)
        {
            int position = PbtLayout.TrieNodeGroupBoundarySlotPosition(slot);
            Assert.That(PbtLayout.TrieNodeGroupIsBoundaryPosition(position), $"slot {slot} at position {position}");
            Assert.That(PbtLayout.TrieNodeGroupBoundarySlot(position), Is.EqualTo(slot));
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

        PbtTrieNodeGroup.ValueSlot[] slots = new PbtTrieNodeGroup.ValueSlot[Layout.PositionCount];
        slots[13] = PbtTrieNodeGroup.InternalSlot(hashB);
        slots[29] = PbtTrieNodeGroup.StemSlot(stemA, rootA);
        slots[PbtLayout.TrieNodeGroupBoundarySlotPosition(0)] = PbtTrieNodeGroup.InternalSlot(hashC);
        slots[PbtLayout.TrieNodeGroupBoundarySlotPosition(1)] = PbtTrieNodeGroup.StemSlot(stemB, rootB);

        // the subtree holds more stems than this group's two stem nodes: the boundary internal at slot 0
        // points at a child group holding the rest
        PbtSubtreeStats stats = new(9);

        byte[] encoded = new byte[PbtTrieNodeGroup<Layout>.MaxEncodedLength];
        int length = Encode(slots, stats, encoded, format);
        Assert.That(length, Is.EqualTo(TrailerLength + 4 * 32 + 2 * 31));

        PbtTrieNodeGroup<Layout> decoded = PbtTrieNodeGroup<Layout>.Decode(encoded.AsSpan(0, length));
        Assert.That(decoded.Stats, Is.EqualTo(stats));
        Assert.That(decoded.Format, Is.EqualTo(format));
        PbtTrieNodeGroup.ValueSlot[] roundTripped = new PbtTrieNodeGroup.ValueSlot[Layout.PositionCount];
        for (int position = 0; position < Layout.PositionCount; position++)
        {
            PbtTrieNodeGroup.ValueSlot slot = decoded[position].ToValue();
            Assert.That(slot.Kind, Is.EqualTo(slots[position].Kind), $"kind at {position}");
            Assert.That(slot.Stem, Is.EqualTo(slots[position].Stem), $"stem at {position}");
            Assert.That(slot.Hash, Is.EqualTo(slots[position].Hash), $"hash at {position}");
            roundTripped[position] = slot;
        }

        byte[] reencoded = new byte[PbtTrieNodeGroup<Layout>.MaxEncodedLength];
        Assert.That(Encode(roundTripped, decoded.Stats, reencoded, format), Is.EqualTo(length));
        Assert.That(reencoded.AsSpan(0, length).SequenceEqual(encoded.AsSpan(0, length)));

        // an empty group encodes to nothing (the store's removal marker), whatever it is told it holds
        Assert.That(new PbtGroupEncoder<Layout>(encoded, format).Finish(stats), Is.EqualTo(0));
        Assert.That(default(PbtTrieNodeGroup<Layout>).Stats, Is.EqualTo(default(PbtSubtreeStats)), "an absent subtree holds nothing");

        // validation: an unknown format byte, position bit 31, a stem bit without its presence bit,
        // and a length that does not match the bitmaps are all rejected
        byte[] valid = encoded[..length];
        byte[] badFormat = (byte[])valid.Clone();
        badFormat[^1] = 0xFF;
        Assert.That(() => PbtTrieNodeGroup<Layout>.Decode(badFormat), Throws.TypeOf<InvalidDataException>());

        // the whole header sits in the trailer: presence, then stems, then the runs, then the six-byte
        // stats, then the format byte
        int trailer = length - TrailerLength;

        byte[] highBit = (byte[])valid.Clone();
        highBit[trailer + 3] |= 0x80;
        Assert.That(() => PbtTrieNodeGroup<Layout>.Decode(highBit), Throws.TypeOf<InvalidDataException>());

        byte[] orphanStem = (byte[])valid.Clone();
        orphanStem[trailer + 4] |= 0x04; // stem bit for absent position 2
        Assert.That(() => PbtTrieNodeGroup<Layout>.Decode(orphanStem), Throws.TypeOf<InvalidDataException>());

        Assert.That(() => PbtTrieNodeGroup<Layout>.Decode(valid.AsSpan(..^1)), Throws.TypeOf<InvalidDataException>());
        Assert.That(() => PbtTrieNodeGroup<Layout>.Decode(valid.AsSpan(..4)), Throws.TypeOf<InvalidDataException>());
    }

    /// <summary>
    /// The skip mask marks exactly the levels the format folds — the odd ones, or every one but the
    /// boundary — derived here from the fold's own recursion rather than restated, so that the two
    /// parameterisations of it must agree.
    /// </summary>
    /// <param name="skippedCount">
    /// Positions with no stored internal node: none at every level, levels 1 and 3's two and eight when
    /// interleaved, and all fifteen inner ones — the folded root included — at the boundary alone.
    /// </param>
    [TestCase(PbtGroupFormat.EveryLevel, 0)]
    [TestCase(PbtGroupFormat.Interleaved, 10)]
    [TestCase(PbtGroupFormat.BoundaryOnly, 15)]
    public void SkippedPositions_AreExactlyTheLevelsTheFormatFolds(PbtGroupFormat format, int skippedCount)
    {
        uint skipped = 0;
        uint kept = 0;
        Walk(Layout.RootPosition, Layout.BoundarySlots, 0);

        Assert.That(BitOperations.PopCount(skipped), Is.EqualTo(skippedCount));
        Assert.That(BitOperations.PopCount(kept), Is.EqualTo(Layout.PositionCount - skippedCount));
        Assert.That(skipped & kept, Is.Zero, "a position is either stored or folded, never both");
        Assert.That(kept | skipped, Is.EqualTo((1u << Layout.PositionCount) - 1), "every position is accounted for");

        // a boundary slot is a level of its own and is always stored, whatever the format
        for (int slot = 0; slot < Layout.BoundarySlots; slot++)
        {
            Assert.That(
                PbtLayout.TrieNodeGroupIsSkippedPosition(format, PbtLayout.TrieNodeGroupBoundarySlotPosition(slot)),
                Is.False, $"boundary slot {slot}");
        }

        void Walk(int position, int width, int level)
        {
            bool storesInternal = PbtLayout.TrieNodeGroupStoresInternalAtWidth(format, width);
            Assert.That(
                PbtLayout.TrieNodeGroupIsSkippedPosition(format, position), Is.EqualTo(!storesInternal),
                $"position {position} at level {level} (width {width}): the by-position and by-width answers must agree");
            Assert.That(
                storesInternal,
                Is.EqualTo(format switch
                {
                    PbtGroupFormat.Interleaved => level % 2 == 0,
                    PbtGroupFormat.BoundaryOnly => width == 1,
                    _ => true,
                }),
                $"level {level} is {(storesInternal ? "kept" : "skipped")}");

            if (storesInternal) kept |= 1u << position; else skipped |= 1u << position;
            if (width == 1) return;

            Walk(position - width, width / 2, level + 1);
            Walk(position - 1, width / 2, level + 1);
        }
    }

    /// <summary>
    /// A group's encoding is told from a run's and from a cluster's by the byte it ends with, so no
    /// format may take one of theirs — a group whose last byte reads as a cluster is parsed as one, and
    /// what it says its group's length is is then whatever those bytes happen to hold.
    /// </summary>
    [TestCase(PbtGroupFormat.EveryLevel)]
    [TestCase(PbtGroupFormat.Interleaved)]
    [TestCase(PbtGroupFormat.BoundaryOnly)]
    public void FormatByte_IsNeitherARunsNorAClusters(PbtGroupFormat format)
    {
        byte[] encoding = [(byte)format];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(PbtNodeChain.IsChain(encoding), Is.False);
            Assert.That(PbtNodeCluster.HoldsChildren(encoding), Is.False);
        }
    }

    /// <summary>
    /// A group folds the levels its format skips rather than storing them, so an internal node at one
    /// is not a thing the encoding can say — while a stem there is, and must survive.
    /// </summary>
    /// <param name="position">
    /// An inner position <paramref name="format"/> folds and the every-level encoding stores: level 1
    /// for the interleaved format, and a level-2 one — which that format keeps — for the boundary-only
    /// format, which keeps no inner level at all.
    /// </param>
    [TestCase(PbtGroupFormat.Interleaved, 14)]
    [TestCase(PbtGroupFormat.BoundaryOnly, 13)]
    public void RejectsAnInternalNodeAtASkippedLevel(PbtGroupFormat format, int position)
    {
        ValueHash256 hash = new(Bytes.FromHexString("0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        Stem stem = new(Bytes.FromHexString("0x11111111111111111111111111111111111111111111111111111111111111"));
        PbtSubtreeStats stats = new(2);

        PbtTrieNodeGroup.ValueSlot[] slots = new PbtTrieNodeGroup.ValueSlot[Layout.PositionCount];
        slots[position] = PbtTrieNodeGroup.InternalSlot(hash);

        // the every-level format is what such an encoding can only be, and it still decodes
        byte[] encoded = new byte[PbtTrieNodeGroup<Layout>.MaxEncodedLength];
        int length = Encode(slots, stats, encoded, PbtGroupFormat.EveryLevel);
        Assert.That(PbtTrieNodeGroup<Layout>.Decode(encoded.AsSpan(0, length)).Format, Is.EqualTo(PbtGroupFormat.EveryLevel));

        // relabelling those same bytes is rejected: the position holds an internal node
        byte[] mislabelled = encoded[..length];
        mislabelled[^1] = (byte)format;
        Assert.That(() => PbtTrieNodeGroup<Layout>.Decode(mislabelled), Throws.TypeOf<InvalidDataException>());

        // a stem at that very position is legal in both: nothing recomputes a stem
        slots[position] = PbtTrieNodeGroup.StemSlot(stem, hash);
        foreach (PbtGroupFormat stemFormat in (PbtGroupFormat[])[PbtGroupFormat.EveryLevel, format])
        {
            int stemLength = Encode(slots, stats, encoded, stemFormat);
            PbtTrieNodeGroup<Layout> decoded = PbtTrieNodeGroup<Layout>.Decode(encoded.AsSpan(0, stemLength));
            Assert.That(decoded.KindAt(position), Is.EqualTo(PbtTrieNodeGroup.NodeKind.Stem), $"{stemFormat}");
            Assert.That(decoded[position].Stem, Is.EqualTo(stem), $"{stemFormat}");
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

        PbtTrieNodeGroup.ValueSlot[] slots = new PbtTrieNodeGroup.ValueSlot[Layout.PositionCount];
        byte[] encoded = new byte[PbtTrieNodeGroup<Layout>.MaxEncodedLength];

        slots[Layout.RootPosition] = PbtTrieNodeGroup.InternalSlot(hash);
        foreach (PbtGroupFormat format in Formats)
        {
            int length = Encode(slots, stats, encoded, format);
            Assert.That(() => PbtTrieNodeGroup<Layout>.Decode(encoded.AsSpan(0, length)), Throws.TypeOf<InvalidDataException>(), $"{format}");
        }

        // a stem at the root position is legal in both and round-trips: nothing recomputes a stem
        slots[Layout.RootPosition] = PbtTrieNodeGroup.StemSlot(stem, hash);
        foreach (PbtGroupFormat format in Formats)
        {
            int length = Encode(slots, stats, encoded, format);
            PbtTrieNodeGroup<Layout> decoded = PbtTrieNodeGroup<Layout>.Decode(encoded.AsSpan(0, length));
            Assert.That(decoded.KindAt(Layout.RootPosition), Is.EqualTo(PbtTrieNodeGroup.NodeKind.Stem), $"{format}");
            Assert.That(decoded[Layout.RootPosition].Stem, Is.EqualTo(stem), $"{format}");
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
        // the numbering runs on past a narrow tile — slot 16 sits at position 31 — so only the
        // positions this one holds are its business
        positions &= (1u << Layout.PositionCount) - 1;

        ulong expected = 0;
        for (int position = 0; position < Layout.PositionCount; position++)
        {
            if ((positions >> position & 1) == 0 || !PbtLayout.TrieNodeGroupIsBoundaryPosition(position)) continue;
            expected |= 1UL << PbtLayout.TrieNodeGroupBoundarySlot(position);
        }

        Assert.That(PbtLayout.GatherBoundary(positions, Layout.BoundarySlots), Is.EqualTo(expected), $"0x{positions:x8}");
        Assert.That(expected >> Layout.BoundarySlots, Is.Zero, "only the sixteen slot bits are set");
    }

    [Test]
    public void BoundaryShape_EmptyGroup_IsUnoccupied() =>
        Assert.That(default(PbtTrieNodeGroup<Layout>).BoundaryShape(), Is.EqualTo(default(BoundarySlotMasks)));

    /// <summary>
    /// A run is held whole by the boundary slot it hangs from, so its entry is longer than the hash a
    /// pointer to a child group is — which the entries after it must be found past, and which the
    /// bitmaps alone have to say. Its cached node hash still ends it, so the slot reads as a boundary
    /// internal wherever only the hash is wanted.
    /// </summary>
    [TestCase(PbtGroupFormat.EveryLevel)]
    [TestCase(PbtGroupFormat.Interleaved)]
    [TestCase(PbtGroupFormat.BoundaryOnly)]
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
        PbtNodeChain.Write<Layout>(chain, startDepth: 8, targetDepth: 20, targetPath, childRoot, new PbtSubtreeStats(3));

        // a stem below the run and a boundary internal above it, so the run shifts the one that follows
        // and leaves the one before it where it was
        byte[] encoded = new byte[PbtTrieNodeGroup<Layout>.MaxEncodedLength];
        PbtGroupEncoder<Layout> builder = new(encoded, format);
        builder.AppendStem(PbtLayout.TrieNodeGroupBoundarySlotPosition(0), stem, stemRoot);
        builder.AppendChain(PbtLayout.TrieNodeGroupBoundarySlotPosition(chainSlot), chain);
        builder.AppendInternal(PbtLayout.TrieNodeGroupBoundarySlotPosition(2), childRoot);
        int length = builder.Finish(stats);

        PbtTrieNodeGroup<Layout> group = PbtTrieNodeGroup<Layout>.Decode(encoded.AsSpan(0, length));
        Assert.That(length, Is.EqualTo(TrailerLength + 2 * 32 + Stem.Length + PbtNodeChain.EncodedLength));

        int chainPosition = PbtLayout.TrieNodeGroupBoundarySlotPosition(chainSlot);
        Assert.That(group.KindAt(chainPosition), Is.EqualTo(PbtTrieNodeGroup.NodeKind.Chain));
        Assert.That(group[chainPosition].ChainData.SequenceEqual(chain));
        Assert.That(group[chainPosition].Hash, Is.EqualTo(PbtNodeChain.NodeHashOf(chain)), "a run contributes its cached node hash");
        Assert.That(group[chainPosition].NodeHash(), Is.EqualTo(PbtNodeChain.NodeHashOf(chain)));

        // the slots around it are unmoved and unconfused
        Assert.That(group[PbtLayout.TrieNodeGroupBoundarySlotPosition(0)].Stem, Is.EqualTo(stem));
        Assert.That(group.KindAt(PbtLayout.TrieNodeGroupBoundarySlotPosition(2)), Is.EqualTo(PbtTrieNodeGroup.NodeKind.Internal));
        Assert.That(group[PbtLayout.TrieNodeGroupBoundarySlotPosition(2)].Hash, Is.EqualTo(childRoot));

        BoundarySlotMasks boundary = group.BoundaryShape();
        Assert.That(boundary, Is.EqualTo(new BoundarySlotMasks(0b111UL, 0b001UL, 1UL << chainSlot)));
        Assert.That(boundary.ChildSlots, Is.EqualTo(1UL << 2), "a run roots no child blob, and neither does a stem");

        // the run bitmap must name an occupied boundary slot that holds no stem
        byte[] valid = encoded[..length];
        foreach (int slot in (int[])[0, 3])
        {
            byte[] misplaced = (byte[])valid.Clone();
            BinaryPrimitives.WriteUInt16LittleEndian(misplaced.AsSpan(length - TrailerLength + 8), (ushort)(1 << slot));
            Assert.That(() => PbtTrieNodeGroup<Layout>.Decode(misplaced), Throws.TypeOf<InvalidDataException>(), $"a run claimed at slot {slot}");
        }
    }

    /// <summary>
    /// The bound a producer reserves room by must cover the densest group its boundary admits: a node
    /// at every range an occupied slot reaches, the boundary slots all holding the longest entry of the
    /// kind under test. A full tile is where it must be tight — every level full, and the only room left
    /// over the root's, which is folded rather than stored.
    /// </summary>
    [TestCase(0xFFFFu, PbtTrieNodeGroup.NodeKind.Internal)]
    [TestCase(0xFFFFu, PbtTrieNodeGroup.NodeKind.Stem)]
    [TestCase(0xFFFFu, PbtTrieNodeGroup.NodeKind.Chain)]
    [TestCase(0x0001u, PbtTrieNodeGroup.NodeKind.Internal)] // a lone slot, reached down a spine of single-child levels
    [TestCase(0x8001u, PbtTrieNodeGroup.NodeKind.Stem)]     // two spines, sharing only the root
    [TestCase(0x0007u, PbtTrieNodeGroup.NodeKind.Chain)]
    [TestCase(0xAAAAu, PbtTrieNodeGroup.NodeKind.Internal)]
    public void EncodedLengthBound_CoversTheDensestGroupItsBoundaryAdmits(uint occupiedSlots, PbtTrieNodeGroup.NodeKind boundaryKind)
    {
        ValueHash256 hash = new(Bytes.FromHexString("0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        Stem stem = new(Bytes.FromHexString("0x11111111111111111111111111111111111111111111111111111111111111"));
        Stem targetPath = new(Bytes.FromHexString("0x0dead000000000000000000000000000000000000000000000000000000000"));
        byte[] chain = new byte[PbtNodeChain.EncodedLength];
        PbtNodeChain.Write<Layout>(chain, startDepth: 8, targetDepth: 20, targetPath, hash, new PbtSubtreeStats(3));

        // every position whose range holds an occupied slot, bar the root, which no group stores
        uint positions = OccupiedPositions(occupiedSlots) & ~(1u << Layout.RootPosition);

        byte[] encoded = new byte[PbtTrieNodeGroup<Layout>.MaxEncodedLength];
        PbtGroupEncoder<Layout> builder = new(encoded, PbtGroupFormat.EveryLevel);
        for (int position = 0; position < Layout.PositionCount; position++)
        {
            if ((positions >> position & 1) == 0) continue;

            switch (PbtLayout.TrieNodeGroupIsBoundaryPosition(position) ? boundaryKind : PbtTrieNodeGroup.NodeKind.Internal)
            {
                case PbtTrieNodeGroup.NodeKind.Stem:
                    builder.AppendStem(position, stem, hash);
                    break;
                case PbtTrieNodeGroup.NodeKind.Chain:
                    builder.AppendChain(position, chain);
                    break;
                default:
                    builder.AppendInternal(position, hash);
                    break;
            }
        }

        int length = builder.Finish(new PbtSubtreeStats(9));
        PbtTrieNodeGroup<Layout> group = PbtTrieNodeGroup<Layout>.Decode(encoded.AsSpan(0, length));
        int bound = PbtTrieNodeGroup<Layout>.EncodedLengthBound(group.BoundaryShape());

        Assert.That(bound, Is.GreaterThanOrEqualTo(length));
        Assert.That(bound, Is.LessThanOrEqualTo(PbtTrieNodeGroup<Layout>.MaxEncodedLength));
        if (occupiedSlots == (1u << Layout.BoundarySlots) - 1)
        {
            Assert.That(bound - length, Is.EqualTo(32), "a full tile leaves the bound only the root hash to spare, which no group stores");
        }
    }

    /// <summary>Every position whose range holds one of <paramref name="occupiedSlots"/>, the group root included.</summary>
    private static uint OccupiedPositions(uint occupiedSlots)
    {
        uint positions = 0;
        Walk(Layout.RootPosition, 0, Layout.BoundarySlots);
        return positions;

        void Walk(int position, int firstSlot, int width)
        {
            if ((occupiedSlots & (((1u << width) - 1) << firstSlot)) == 0) return;

            positions |= 1u << position;
            if (width == 1) return;

            Walk(position - width, firstSlot, width / 2);
            Walk(position - 1, firstSlot + width / 2, width / 2);
        }
    }

    /// <summary>
    /// Encodes positional slots through <see cref="PbtGroupEncoder"/>, walking positions in
    /// the ascending order it requires — the order the updater's post-order rebuild appends in.
    /// </summary>
    private static int Encode(
        ReadOnlySpan<PbtTrieNodeGroup.ValueSlot> slots, in PbtSubtreeStats stats, Span<byte> destination, PbtGroupFormat format)
    {
        PbtGroupEncoder<Layout> builder = new(destination, format);
        for (int position = 0; position < Layout.PositionCount; position++)
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
