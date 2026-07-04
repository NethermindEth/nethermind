// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.State.Flat.History.Segmented;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class SegmentHistoryStoreTests
{
    private const int KeyLen = 4;
    private static readonly byte[] KeyA = [1, 2, 3, 4];
    private static readonly byte[] KeyB = [9, 9, 9, 9];

    private string _dir = null!;

    [SetUp]
    public void SetUp() => _dir = Directory.CreateTempSubdirectory("ef-history-").FullName;

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private SegmentHistoryStore NewStore(int stepBlocks = 3, int maxSegmentsBeforeMerge = 16) =>
        new(_dir, KeyLen, stepBlocks, maxBufferBytes: long.MaxValue, maxSegmentsBeforeMerge);

    // KeyA: 0xAA @2, 0xBBCC @5, tombstone @9.  KeyB: 0x11 @3.  Every block 1..10 is "completed" as the writer would.
    private static void Populate(SegmentHistoryStore store)
    {
        for (ulong block = 1; block <= 10; block++)
        {
            switch (block)
            {
                case 2: store.RecordChange(2, KeyA, [0xAA]); break;
                case 3: store.RecordChange(3, KeyB, [0x11]); break;
                case 5: store.RecordChange(5, KeyA, [0xBB, 0xCC]); break;
                case 9: store.RecordChange(9, KeyA, ReadOnlySpan<byte>.Empty); break;
            }
            store.CompleteBlock(block);
        }
        store.Flush();
    }

    private static (int written, string hex) AsOf(SegmentHistoryStore store, byte[] key, ulong block)
    {
        Span<byte> buffer = stackalloc byte[64];
        int written = store.TryGetAt(block, key, buffer, out _);
        return (written, written > 0 ? Convert.ToHexString(buffer[..written]).ToLowerInvariant() : "");
    }

    [TestCase(16)] // no merge: three sealed segments [1,3] [4,6] [7,9]
    [TestCase(1)]  // aggressive merge down to a single segment after each seal
    public void Resolves_as_of_block_across_segments(int maxSegments)
    {
        using SegmentHistoryStore store = NewStore(maxSegmentsBeforeMerge: maxSegments);
        Populate(store);

        Assert.That(AsOf(store, KeyA, 1).written, Is.EqualTo(-1)); // before first change -> fall back to tip
        Assert.That(AsOf(store, KeyA, 2).hex, Is.EqualTo("aa"));
        Assert.That(AsOf(store, KeyA, 4).hex, Is.EqualTo("aa"));   // spans an older segment
        Assert.That(AsOf(store, KeyA, 5).hex, Is.EqualTo("bbcc"));
        Assert.That(AsOf(store, KeyA, 8).hex, Is.EqualTo("bbcc")); // spans an older segment
        Assert.That(AsOf(store, KeyA, 9).written, Is.EqualTo(0));  // tombstone
        Assert.That(AsOf(store, KeyA, 10).written, Is.EqualTo(0));

        Assert.That(AsOf(store, KeyB, 2).written, Is.EqualTo(-1));
        Assert.That(AsOf(store, KeyB, 3).hex, Is.EqualTo("11"));
        Assert.That(AsOf(store, KeyB, 10).hex, Is.EqualTo("11"));
    }

    [Test]
    public void Serves_reads_from_the_unsealed_buffer()
    {
        using SegmentHistoryStore store = NewStore(stepBlocks: 1000); // never seals
        store.RecordChange(2, KeyA, [0xAA]);
        store.CompleteBlock(2);

        Assert.That(AsOf(store, KeyA, 2).hex, Is.EqualTo("aa"));
        Assert.That(AsOf(store, KeyA, 1).written, Is.EqualTo(-1));
    }

    [Test]
    public void Survives_reopen()
    {
        using (SegmentHistoryStore store = NewStore())
            Populate(store);

        using SegmentHistoryStore reopened = NewStore();
        Assert.That(AsOf(reopened, KeyA, 4).hex, Is.EqualTo("aa"));
        Assert.That(AsOf(reopened, KeyA, 9).written, Is.EqualTo(0));
        Assert.That(AsOf(reopened, KeyB, 3).hex, Is.EqualTo("11"));
        Assert.That(reopened.CoversBlock(5), Is.True);
        Assert.That(reopened.CoversBlock(100), Is.False);
    }

    [Test]
    public void HasChangeInRange_detects_clears()
    {
        using SegmentHistoryStore clears = NewStore();
        for (ulong block = 1; block <= 10; block++)
        {
            if (block == 4) clears.RecordChange(4, KeyA, ReadOnlySpan<byte>.Empty);
            clears.CompleteBlock(block);
        }
        clears.Flush();

        Assert.That(clears.HasChangeInRange(KeyA, 2, 6), Is.True);   // clear at 4 lies in (2, 6]
        Assert.That(clears.HasChangeInRange(KeyA, 4, 6), Is.False);  // lower bound is exclusive: 4 is not in (4, 6]
        Assert.That(clears.HasChangeInRange(KeyA, 3, 4), Is.True);   // 4 is in (3, 4]
        Assert.That(clears.HasChangeInRange(KeyA, 5, 9), Is.False);  // no clear in (5, 9]
        Assert.That(clears.HasChangeInRange(KeyB, 0, 10), Is.False); // KeyB never cleared
    }
}
