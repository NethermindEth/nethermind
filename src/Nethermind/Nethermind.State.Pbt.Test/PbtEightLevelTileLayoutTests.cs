// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtEightLevelTileLayoutTests
{
    [Test]
    public void WideGroupRoundTripsBoundaryShape()
    {
        const int slot = 200;
        int position = PbtLayout.TrieNodeGroupBoundarySlotPosition(slot);
        Span<ulong> presence = stackalloc ulong[PbtEightLevelTileLayout.PositionMaskWordCount];
        Span<ulong> stems = stackalloc ulong[PbtEightLevelTileLayout.PositionMaskWordCount];
        Span<ulong> chains = stackalloc ulong[PbtEightLevelTileLayout.BoundaryMaskWordCount];
        PbtBitset.Set(presence, position);
        PbtBitset.Set(stems, position);

        int maskLength = NodeGroupMaskEncoding.EncodedLength<PbtEightLevelTileLayout>(chains);
        byte[] encoded = new byte[PbtTrieNodeGroup.Slot.StemLength + maskLength + PbtSubtreeStats.EncodedLength + sizeof(byte)];
        encoded[Stem.Length] = 1;
        int written = NodeGroupMaskEncoding.Write<PbtEightLevelTileLayout>(
            encoded.AsSpan(PbtTrieNodeGroup.Slot.StemLength), presence, stems, chains);
        encoded[^1] = (byte)PbtGroupFormat.EveryLevel;

        PbtTrieNodeGroup<PbtEightLevelTileLayout> group = PbtTrieNodeGroup<PbtEightLevelTileLayout>.Decode(encoded);
        Span<ulong> copiedPresence = stackalloc ulong[PbtEightLevelTileLayout.BoundaryMaskWordCount];
        Span<ulong> copiedStems = stackalloc ulong[PbtEightLevelTileLayout.BoundaryMaskWordCount];
        Span<ulong> copiedChains = stackalloc ulong[PbtEightLevelTileLayout.BoundaryMaskWordCount];
        group.CopyBoundaryShape(copiedPresence, copiedStems, copiedChains);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.EqualTo(maskLength));
            Assert.That(group.BoundaryKindAt(slot), Is.EqualTo(PbtTrieNodeGroup.NodeKind.Stem));
            Assert.That(PbtBitset.Contains(copiedPresence, slot), Is.True);
            Assert.That(PbtBitset.Contains(copiedStems, slot), Is.True);
            Assert.That(PbtBitset.Any(copiedChains), Is.False);
        }
    }
}
