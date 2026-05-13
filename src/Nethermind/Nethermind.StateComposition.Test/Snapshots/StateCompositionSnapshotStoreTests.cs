// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Diff;
using Nethermind.StateComposition.Snapshots;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test.Snapshots;

[TestFixture]
public class StateCompositionSnapshotStoreTests
{
    private static CumulativeTrieStats StatsWith(long codeBytes) => new(
        AccountsTotal: 1, ContractsTotal: 1, StorageSlotsTotal: 1,
        AccountTrieBranches: 0, AccountTrieExtensions: 0, AccountTrieLeaves: 0, AccountTrieBytes: 0,
        StorageTrieBranches: 0, StorageTrieExtensions: 0, StorageTrieLeaves: 0, StorageTrieBytes: 0,
        ContractsWithStorage: 0, EmptyAccounts: 0)
    { CodeBytesTotal = codeBytes, SlotCountHistogram = ImmutableArray.Create(new long[16]) };

    private static StateCompositionSnapshot BuildSnapshot(
        long blockNumber,
        Dictionary<ValueHash256, long>? slotCountByAddress = null,
        Dictionary<ValueHash256, int>? codeHashRefcounts = null,
        Dictionary<ValueHash256, int>? codeHashSizes = null) =>
        new(StatsWith(0), blockNumber, Keccak.Compute($"root-{blockNumber}"),
            DiffsSinceBaseline: 0, ScanBlockNumber: blockNumber,
            DepthStats: new CumulativeDepthStats(),
            SlotCountByAddress: slotCountByAddress ?? [],
            CodeHashRefcounts: codeHashRefcounts ?? [],
            CodeHashSizes: codeHashSizes ?? []);

    [Test]
    public void WriteAndReadLatest_PreservesAllMaps()
    {
        StateCompositionSnapshotStore store = new(new MemDb(), LimboLogs.Instance, entriesPerChunk: 1024);

        ValueHash256 a = Keccak.Compute("a").ValueHash256;
        ValueHash256 b = Keccak.Compute("b").ValueHash256;
        StateCompositionSnapshot snapshot = BuildSnapshot(
            blockNumber: 100,
            slotCountByAddress: new() { [a] = 7, [b] = 1_000_000_000_000 },
            codeHashRefcounts: new() { [a] = 3, [b] = 1 },
            codeHashSizes: new() { [a] = 24_000, [b] = 1 });

        store.WriteSnapshot(snapshot);
        StateCompositionSnapshot? loaded = store.ReadLatestSnapshot();

        Assert.That(loaded, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(loaded!.Value.BlockNumber, Is.EqualTo(100));
            Assert.That(loaded.Value.SlotCountByAddress[a], Is.EqualTo(7));
            Assert.That(loaded.Value.SlotCountByAddress[b], Is.EqualTo(1_000_000_000_000));
            Assert.That(loaded.Value.CodeHashRefcounts[a], Is.EqualTo(3));
            Assert.That(loaded.Value.CodeHashRefcounts[b], Is.EqualTo(1));
            Assert.That(loaded.Value.CodeHashSizes[a], Is.EqualTo(24_000));
            Assert.That(loaded.Value.CodeHashSizes[b], Is.EqualTo(1));
        }
    }

    [Test]
    public void WriteAndReadLatest_LargeMapAcrossManyChunks_PreservesEveryEntry()
    {
        const int totalEntries = 5_000;
        const int chunkSize = 250; // 20 chunks per map
        StateCompositionSnapshotStore store = new(new MemDb(), LimboLogs.Instance, entriesPerChunk: chunkSize);

        Dictionary<ValueHash256, long> slotCounts = new(totalEntries);
        Dictionary<ValueHash256, int> codeRefcounts = new(totalEntries);
        Dictionary<ValueHash256, int> codeSizes = new(totalEntries);
        for (int i = 0; i < totalEntries; i++)
        {
            ValueHash256 hash = Keccak.Compute($"entry-{i}").ValueHash256;
            slotCounts[hash] = i + 1L;
            codeRefcounts[hash] = i + 2;
            codeSizes[hash] = i + 3;
        }

        store.WriteSnapshot(BuildSnapshot(
            blockNumber: 42,
            slotCountByAddress: slotCounts,
            codeHashRefcounts: codeRefcounts,
            codeHashSizes: codeSizes));

        StateCompositionSnapshot loaded = store.ReadLatestSnapshot()!.Value;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loaded.SlotCountByAddress, Has.Count.EqualTo(totalEntries));
            Assert.That(loaded.CodeHashRefcounts, Has.Count.EqualTo(totalEntries));
            Assert.That(loaded.CodeHashSizes, Has.Count.EqualTo(totalEntries));
            foreach (KeyValuePair<ValueHash256, long> kvp in slotCounts)
            {
                Assert.That(loaded.SlotCountByAddress[kvp.Key], Is.EqualTo(kvp.Value));
                Assert.That(loaded.CodeHashRefcounts[kvp.Key], Is.EqualTo(codeRefcounts[kvp.Key]));
                Assert.That(loaded.CodeHashSizes[kvp.Key], Is.EqualTo(codeSizes[kvp.Key]));
            }
        }
    }

    [Test]
    public void WriteSnapshot_NewBlock_RemovesPriorBlocksMainAndChunks()
    {
        MemDb db = new();
        StateCompositionSnapshotStore store = new(db, LimboLogs.Instance, entriesPerChunk: 100);

        Dictionary<ValueHash256, long> firstSlots = [];
        for (int i = 0; i < 250; i++)
            firstSlots[Keccak.Compute($"first-{i}").ValueHash256] = i;
        store.WriteSnapshot(BuildSnapshot(blockNumber: 10, slotCountByAddress: firstSlots));
        int keysAfterFirst = db.GetAllKeys().Count();

        Dictionary<ValueHash256, long> secondSlots = [];
        for (int i = 0; i < 50; i++)
            secondSlots[Keccak.Compute($"second-{i}").ValueHash256] = i;
        store.WriteSnapshot(BuildSnapshot(blockNumber: 11, slotCountByAddress: secondSlots));

        StateCompositionSnapshot loaded = store.ReadLatestSnapshot()!.Value;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(loaded.BlockNumber, Is.EqualTo(11));
            Assert.That(loaded.SlotCountByAddress, Has.Count.EqualTo(50));
            // First write committed to gen 1; the second write rotates to gen 0
            // and must delete gen 1's main blob + chunks.
            Assert.That(store.ReadSnapshot(1), Is.Null);
            Assert.That(db.GetAllKeys().Count(), Is.LessThan(keysAfterFirst));
        }
    }

    [Test]
    public void PurgeOldEntries_RemovesOrphanGenerationFromCrashMidWrite()
    {
        // Generation rotation: when a crash leaves orphan keys in the
        // non-current generation, PurgeOldEntries (called at boot) must remove
        // them so disk usage stays bounded to one generation.
        MemDb db = new();
        StateCompositionSnapshotStore store = new(db, LimboLogs.Instance, entriesPerChunk: 100);

        Dictionary<ValueHash256, long> slots = [];
        for (int i = 0; i < 250; i++)
            slots[Keccak.Compute($"slot-{i}").ValueHash256] = i;
        store.WriteSnapshot(BuildSnapshot(blockNumber: 7, slotCountByAddress: slots));

        // After one write, LatestKey points at gen=1 (first write flips 0→1).
        // Inject an orphan chunk at gen=0 — what a crashed mid-write would leave.
        byte[] orphanGen0Chunk = [0x00, 0x01, 0, 0, 0, 0]; // <gen=0><kind=SlotCount><chunkIdx=0>
        db.Set(orphanGen0Chunk, [0, 0, 0, 0]);

        store.PurgeOldEntries();

        Assert.That(db.GetAllKeys().Any(k => k.SequenceEqual(orphanGen0Chunk)), Is.False,
            "Orphan gen 0 chunk must be purged by boot-time PurgeOldEntries.");
        Assert.That(store.ReadLatestSnapshot()!.Value.BlockNumber, Is.EqualTo(7));
    }

    [Test]
    public void ReadSnapshot_TruncatedChunk_DegradesToEmptyMapsInsteadOfThrowing()
    {
        // Disk-read boundary: a snapshot whose chunk header was written but the
        // payload was truncated mid-flush must not propagate
        // ArgumentOutOfRangeException to the startup path. The reader bails to
        // "no more entries" so the plugin can fall back to a fresh scan.
        MemDb db = new();
        StateCompositionSnapshotStore store = new(db, LimboLogs.Instance, entriesPerChunk: 100);

        Dictionary<ValueHash256, long> slots = [];
        for (int i = 0; i < 4; i++)
            slots[Keccak.Compute($"slot-{i}").ValueHash256] = i;
        store.WriteSnapshot(BuildSnapshot(blockNumber: 1, slotCountByAddress: slots));

        // First write commits at gen=1. Overwrite that gen's chunk 0 with a
        // truncated payload to simulate a partial flush.
        byte[] truncatedChunkKey = [0x01, 0x01, 0, 0, 0, 0]; // <gen=1><kind=SlotCount><chunkIdx=0>
        db.Set(truncatedChunkKey, [0, 0, 0, 4, 0xAA, 0xBB, 0xCC]);

        StateCompositionSnapshot loaded = store.ReadLatestSnapshot()!.Value;
        Assert.That(loaded.SlotCountByAddress, Is.Empty);
    }

    [Test]
    public void ReadSnapshot_MidSequenceTruncatedChunk_KeepsPriorEntries()
    {
        // First chunk is intact; chunk 1 is truncated mid-flush. Reader must
        // stop at the corruption boundary, keep chunk 0's entries, and not
        // throw — the plugin will rescan to repair the missing tail.
        MemDb db = new();
        StateCompositionSnapshotStore store = new(db, LimboLogs.Instance, entriesPerChunk: 2);

        Dictionary<ValueHash256, long> slots = [];
        for (int i = 0; i < 2; i++)
            slots[Keccak.Compute($"slot-{i}").ValueHash256] = i + 1;
        store.WriteSnapshot(BuildSnapshot(blockNumber: 1, slotCountByAddress: slots));

        // Inject a truncated chunk 1 at the live gen (gen=1 after one write).
        byte[] truncatedChunk1 = [0x01, 0x01, 0, 0, 0, 1]; // <gen=1><kind=SlotCount><chunkIdx=1>
        db.Set(truncatedChunk1, [0, 0, 0, 2, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE]);

        StateCompositionSnapshot loaded = store.ReadLatestSnapshot()!.Value;
        Assert.That(loaded.SlotCountByAddress, Has.Count.EqualTo(2),
            "Chunk 0's entries survive; truncated chunk 1 stops the iteration without throwing.");
    }

    [Test]
    public void EmptyMaps_AreNotPersistedAsChunks()
    {
        MemDb db = new();
        StateCompositionSnapshotStore store = new(db, LimboLogs.Instance, entriesPerChunk: 100);

        store.WriteSnapshot(BuildSnapshot(blockNumber: 1));

        // Only main blob (2-byte gen-prefixed key) + LatestKey (8-byte 0xFF) present.
        Assert.That(db.GetAllKeys().Count(), Is.EqualTo(2));
        StateCompositionSnapshot loaded = store.ReadLatestSnapshot()!.Value;
        Assert.That(loaded.SlotCountByAddress, Is.Empty);
        Assert.That(loaded.CodeHashRefcounts, Is.Empty);
        Assert.That(loaded.CodeHashSizes, Is.Empty);
    }

    [Test]
    public void WriteSnapshot_HundredIterations_BoundsKeyCountToOneGeneration()
    {
        MemDb db = new();
        const int chunkSize = 100;
        StateCompositionSnapshotStore store = new(db, LimboLogs.Instance, entriesPerChunk: chunkSize);

        Dictionary<ValueHash256, long> slots = [];
        for (int i = 0; i < 250; i++)
            slots[Keccak.Compute($"slot-{i}").ValueHash256] = i;

        for (int round = 0; round < 100; round++)
            store.WriteSnapshot(BuildSnapshot(blockNumber: 100 + round, slotCountByAddress: slots));

        // Per snapshot: 1 main blob + 3 chunks (250 entries / 100 chunkSize → 3 chunks per kind, only SlotCount populated → 3 chunks total).
        // After cleanup, only the live gen survives + LatestKey.
        // Live gen: 1 main blob + 3 chunks = 4 keys. Plus LatestKey = 5 keys total.
        int keys = db.GetAllKeys().Count();
        Assert.That(keys, Is.LessThanOrEqualTo(6),
            $"after 100 writes, db should hold ≤6 keys (live gen + LatestKey); got {keys}");
        Assert.That(store.ReadLatestSnapshot()!.Value.BlockNumber, Is.EqualTo(199));
    }

    [Test]
    public void WriteSnapshot_RotatesGenerationsAcrossWrites()
    {
        // The first write commits to gen 1; the second flips back to gen 0;
        // alternating. Reads always go to the gen recorded in LatestKey.
        MemDb db = new();
        StateCompositionSnapshotStore store = new(db, LimboLogs.Instance, entriesPerChunk: 100);

        store.WriteSnapshot(BuildSnapshot(blockNumber: 10));
        byte[] latest = db.Get(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })!;
        Assert.That(latest.Length, Is.EqualTo(9), "new-schema LatestKey value is 9 bytes (gen + blockNumber)");
        byte firstGen = latest[0];

        store.WriteSnapshot(BuildSnapshot(blockNumber: 11));
        latest = db.Get(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })!;
        byte secondGen = latest[0];

        Assert.That(secondGen, Is.Not.EqualTo(firstGen), "second write must flip generation");
        Assert.That(store.ReadLatestSnapshot()!.Value.BlockNumber, Is.EqualTo(11));
    }

}
