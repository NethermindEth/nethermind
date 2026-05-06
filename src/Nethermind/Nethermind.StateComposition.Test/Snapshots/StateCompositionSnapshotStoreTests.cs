// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

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
        // Regression: the previous single-blob encoder summed map sizes into an
        // int and overflowed once the maps exceeded ~50M entries (mainnet-scale).
        // With chunked persistence the per-chunk buffer is bounded, so feeding a
        // map larger than the chunk size must still round-trip every entry.
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
            // Block 10's main key + chunk keys must all be gone.
            Assert.That(store.ReadSnapshot(10), Is.Null);
            Assert.That(db.GetAllKeys().Count(), Is.LessThan(keysAfterFirst));
        }
    }

    [Test]
    public void PurgeOldEntries_KeepsLatestMainAndChunks()
    {
        MemDb db = new();
        StateCompositionSnapshotStore store = new(db, LimboLogs.Instance, entriesPerChunk: 100);

        Dictionary<ValueHash256, long> staleSlots = [];
        for (int i = 0; i < 250; i++)
            staleSlots[Keccak.Compute($"stale-{i}").ValueHash256] = i;
        store.WriteSnapshot(BuildSnapshot(blockNumber: 7, slotCountByAddress: staleSlots));

        // Manually inject an orphaned chunk for a block that is no longer the
        // latest — simulating a crash mid-write. PurgeOldEntries must drop it.
        byte[] orphanChunkKey =
        [
            0, 0, 0, 0, 0, 0, 0, 5,        // blockNumber = 5
            0x01,                            // SlotCountKind
            0, 0, 0, 0,                      // chunk index 0
        ];
        db.Set(orphanChunkKey, [0, 0, 0, 0]); // empty chunk payload

        store.PurgeOldEntries();

        Assert.That(db.GetAllKeys().Any(k => k.SequenceEqual(orphanChunkKey)), Is.False,
            "Orphan chunk for block 5 must be purged.");
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

        byte[] truncatedChunkKey =
        [
            0, 0, 0, 0, 0, 0, 0, 1,        // blockNumber = 1
            0x01,                            // SlotCountKind
            0, 0, 0, 0,                      // chunk index 0
        ];
        // Header claims 4 entries (160 bytes payload) but only 3 bytes follow.
        db.Set(truncatedChunkKey, [0, 0, 0, 4, 0xAA, 0xBB, 0xCC]);

        StateCompositionSnapshot loaded = store.ReadLatestSnapshot()!.Value;
        Assert.That(loaded.SlotCountByAddress, Is.Empty);
    }

    [Test]
    public void EmptyMaps_AreNotPersistedAsChunks()
    {
        MemDb db = new();
        StateCompositionSnapshotStore store = new(db, LimboLogs.Instance, entriesPerChunk: 100);

        store.WriteSnapshot(BuildSnapshot(blockNumber: 1));

        // Only main blob (8-byte key) + LatestKey (8-byte 0xFF) should be present.
        Assert.That(db.GetAllKeys().Count(), Is.EqualTo(2));
        StateCompositionSnapshot loaded = store.ReadLatestSnapshot()!.Value;
        Assert.That(loaded.SlotCountByAddress, Is.Empty);
        Assert.That(loaded.CodeHashRefcounts, Is.Empty);
        Assert.That(loaded.CodeHashSizes, Is.Empty);
    }
}
