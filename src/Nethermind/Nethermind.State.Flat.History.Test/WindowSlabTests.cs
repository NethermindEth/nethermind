// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.History.Proofs;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class WindowSlabTests
{
    [Test]
    public void Reset_AfterTwoWindowsSmallerThanAnEarlierOne_ReturnsTheChunksNeitherUsed()
    {
        using WindowSlab slab = new();
        AllocateAndReset(slab, 10_000);
        int busyChunks = slab.ChunkCount;
        Assert.That(busyChunks, Is.GreaterThan(1));

        AllocateAndReset(slab, 10);
        Assert.That(slab.ChunkCount, Is.EqualTo(busyChunks), "a small flush right after a busy one (the remainder after an early flush at the cap) keeps the chunks");

        AllocateAndReset(slab, 10);
        Assert.That(slab.ChunkCount, Is.EqualTo(1));
    }

    private static void AllocateAndReset(WindowSlab slab, int slots)
    {
        for (int slot = 0; slot < slots; slot++) slab.Allocate();
        slab.Reset();
    }
}
