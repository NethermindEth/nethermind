// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtTrieNodeGroupTests
{
    [Test]
    public void PositionMath_EncodeDecodeRoundTrip_AndValidation()
    {
        // the boundary position math is self-consistent and marks exactly the 16 boundary slots
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

        // a representative mix: root and inner internals, an inner stem, a boundary internal and a
        // boundary stem (ancestry invariants are the updater's concern, not the codec's)
        Stem stemA = new(Bytes.FromHexString("0x11111111111111111111111111111111111111111111111111111111111111"));
        Stem stemB = new(Bytes.FromHexString("0x22222222222222222222222222222222222222222222222222222222222222"));
        ValueHash256 hashA = new(Bytes.FromHexString("0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        ValueHash256 hashB = new(Bytes.FromHexString("0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        ValueHash256 hashC = new(Bytes.FromHexString("0xcccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"));
        ValueHash256 rootA = new(Bytes.FromHexString("0xdddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"));
        ValueHash256 rootB = new(Bytes.FromHexString("0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"));

        PbtTrieNodeGroup group = new();
        group[PbtTrieNodeGroup.RootPosition] = PbtTrieNodeGroup.InternalSlot(hashA);
        group[14] = PbtTrieNodeGroup.InternalSlot(hashB);
        group[29] = PbtTrieNodeGroup.StemSlot(stemA, rootA);
        group[PbtTrieNodeGroup.BoundaryPosition(0)] = PbtTrieNodeGroup.InternalSlot(hashC);
        group[PbtTrieNodeGroup.BoundaryPosition(1)] = PbtTrieNodeGroup.StemSlot(stemB, rootB);

        byte[] encoded = new byte[PbtTrieNodeGroup.MaxEncodedLength];
        int length = group.Encode(encoded);
        Assert.That(length, Is.EqualTo(8 + 5 * 32 + 2 * 31));

        PbtTrieNodeGroup decoded = PbtTrieNodeGroup.Decode(encoded.AsSpan(0, length));
        for (int position = 0; position < PbtTrieNodeGroup.PositionCount; position++)
        {
            Assert.That(decoded[position].Kind, Is.EqualTo(group[position].Kind), $"kind at {position}");
            Assert.That(decoded[position].Stem, Is.EqualTo(group[position].Stem), $"stem at {position}");
            Assert.That(decoded[position].Hash, Is.EqualTo(group[position].Hash), $"hash at {position}");
        }

        // deterministic: re-encoding the decoded group reproduces the bytes exactly
        byte[] reencoded = new byte[PbtTrieNodeGroup.MaxEncodedLength];
        Assert.That(decoded.Encode(reencoded), Is.EqualTo(length));
        Assert.That(reencoded.AsSpan(0, length).SequenceEqual(encoded.AsSpan(0, length)));

        // an empty group encodes to nothing (the store's removal marker)
        Assert.That(new PbtTrieNodeGroup().Encode(encoded), Is.EqualTo(0));

        // validation: position bit 31, a stem bit without its presence bit, and a length that does
        // not match the bitmaps are all rejected
        byte[] valid = encoded[..length];
        byte[] highBit = (byte[])valid.Clone();
        highBit[3] |= 0x80;
        Assert.That(() => PbtTrieNodeGroup.Decode(highBit), Throws.TypeOf<InvalidDataException>());

        byte[] orphanStem = (byte[])valid.Clone();
        orphanStem[4] |= 0x04; // stem bit for absent position 2
        Assert.That(() => PbtTrieNodeGroup.Decode(orphanStem), Throws.TypeOf<InvalidDataException>());

        Assert.That(() => PbtTrieNodeGroup.Decode(valid.AsSpan(..^1)), Throws.TypeOf<InvalidDataException>());
        Assert.That(() => PbtTrieNodeGroup.Decode(valid.AsSpan(..4)), Throws.TypeOf<InvalidDataException>());
    }
}
