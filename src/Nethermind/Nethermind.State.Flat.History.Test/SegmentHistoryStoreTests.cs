// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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

    private SegmentHistoryStore NewStore(int stepBlocks = 3, int mergeFanout = 16, long maxSegmentBlocks = long.MaxValue) =>
        new(_dir, KeyLen, stepBlocks, maxBufferBytes: long.MaxValue, mergeFanout, maxSegmentBlocks);

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

    [TestCase(16)] // no merge: sealed segments [1,3] [4,6] [7,9] [10,10] stay separate
    [TestCase(2)]  // size-tiered merge fuses adjacent same-tier segments as they accrue
    public void Resolves_as_of_block_across_segments(int mergeFanout)
    {
        using SegmentHistoryStore store = NewStore(mergeFanout: mergeFanout);
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
    public void Tiered_merge_bounds_file_count_and_keeps_reads_correct()
    {
        const int stepBlocks = 4;
        const int fanout = 2;
        const long maxSegmentBlocks = 16;
        const ulong lastBlock = 44; // 11 steps: without merge this would be 11 files

        using (SegmentHistoryStore store = NewStore(stepBlocks, fanout, maxSegmentBlocks))
        {
            for (ulong block = 1; block <= lastBlock; block++)
            {
                store.RecordChange(block, KeyA, [(byte)block]); // KeyA changes every block => full history
                store.CompleteBlock(block);
            }
            store.Flush();
        }

        (int from, int to)[] segments = SegmentRanges();

        // Size-tiered + freeze keeps the count logarithmic-ish, not one-per-step, and no segment outgrows the cap.
        Assert.That(segments.Length, Is.LessThanOrEqualTo(6), "file count should stay bounded under tiered merge");
        foreach ((int from, int to) in segments)
            Assert.That(to - from + 1, Is.LessThanOrEqualTo((int)maxSegmentBlocks), "no segment may exceed the freeze cap");
        Assert.That(Array.Exists(segments, s => s.to - s.from + 1 == (int)maxSegmentBlocks), Is.True, "oldest data should be frozen at the cap");

        // Ranges must tile [1, 44] contiguously with no gap or overlap (disjoint ascending invariant).
        Assert.That(segments[0].from, Is.EqualTo(1));
        Assert.That(segments[^1].to, Is.EqualTo((int)lastBlock));
        for (int i = 1; i < segments.Length; i++)
            Assert.That(segments[i].from, Is.EqualTo(segments[i - 1].to + 1), "segments must be gapless");

        using SegmentHistoryStore reopened = NewStore(stepBlocks, fanout, maxSegmentBlocks);
        for (ulong block = 1; block <= lastBlock; block++)
            Assert.That(AsOf(reopened, KeyA, block).hex, Is.EqualTo(Convert.ToHexString([(byte)block]).ToLowerInvariant()), $"@{block}");
    }

    private (int from, int to)[] SegmentRanges()
    {
        List<(int from, int to)> ranges = [];
        foreach (string path in Directory.EnumerateFiles(_dir, "seg_*"))
        {
            string[] parts = Path.GetFileNameWithoutExtension(path).Split('_');
            ranges.Add((int.Parse(parts[1]), int.Parse(parts[2])));
        }
        ranges.Sort((a, b) => a.from.CompareTo(b.from));
        return [.. ranges];
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
