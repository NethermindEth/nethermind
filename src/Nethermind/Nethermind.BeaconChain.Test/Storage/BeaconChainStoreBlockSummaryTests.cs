// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.Storage;

public class BeaconChainStoreBlockSummaryTests
{
    private static readonly Hash256 GloasRoot = Keccak.Compute("gloas block");
    private static readonly Hash256 FuluRoot = Keccak.Compute("fulu block");

    private MemColumnsDb<BeaconChainDbColumns> _db = null!;
    private BeaconChainStore _store = null!;

    [SetUp]
    public void CreateStore()
    {
        _db = new MemColumnsDb<BeaconChainDbColumns>();
        _store = new BeaconChainStore(_db, Sepolia);
    }

    [TearDown]
    public void DisposeStore() => _db.Dispose();

    [Test]
    public void Canonical_retention_preserves_the_request_window_and_replay_anchor([Values] bool gloas, [Values] bool replayBehind, [Values] bool legacyFloor)
    {
        BeaconChainSpec spec = Sepolia with { GloasForkEpoch = gloas ? 128UL : ulong.MaxValue };
        BeaconChainStore store = new(_db, spec);
        const ulong first = 32;
        ulong boundary = replayBehind ? 64UL : 96UL;
        Hash256 old = StoreBlock(store, "expired", first, canonical: true);
        Hash256 before = StoreBlock(store, "before boundary", boundary - 1, canonical: true);
        Hash256 retained = StoreBlock(store, "boundary", boundary, canonical: true);
        store.SetAnchor(old, first);
        if (legacyFloor) store.PutMetadata(BeaconChainMetadataKeys.EarliestBlockSlot, []);
        store.SetAnchor(retained, replayBehind ? boundary : 128);
        ulong retention = gloas ? BeaconChainStore.MinEpochsForBlockRequests : Presets.MinValidatorWithdrawabilityDelay + Presets.ChurnLimitQuotient / 2;

        store.PruneCanonicalBlocks(retention + 3, replayBehind ? boundary : 128);

        BeaconChainStore restarted = new(_db, spec);
        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(restarted.TryGetBlockSummary(old, out _), Is.False);
        Assert.That(restarted.TryGetBlockSummary(before, out _), Is.False);
        Assert.That(restarted.TryGetCanonicalRoot(boundary - 1, out _), Is.False);
        Assert.That(restarted.TryGetBlockSummary(retained, out _), Is.True);
        Assert.That(restarted.TryGetCanonicalRoot(boundary, out Hash256? root), Is.True);
        Assert.That(root, Is.EqualTo(retained));
        Assert.That(restarted.TryGetEarliestBlockSlot(out ulong floor), Is.True);
        Assert.That(floor, Is.EqualTo(boundary));
    }

    [Test]
    public void Canonical_retention_resumes_bounded_batches_after_restart()
    {
        BeaconChainStore store = new(_db, Sepolia with { GloasForkEpoch = 128 });
        Hash256 oldest = StoreBlock(store, "oldest", 0, canonical: true);
        ulong anchorSlot = BeaconChainStore.CanonicalPruneBatchSlots + 10;
        Hash256 anchor = StoreBlock(store, "anchor", anchorSlot, canonical: true);
        store.SetAnchor(oldest, 0);
        store.SetAnchor(anchor, anchorSlot);
        ulong epoch = BeaconChainStore.MinEpochsForBlockRequests + 100;
        store.PruneCanonicalBlocks(epoch, anchorSlot);
        Assert.That(store.TryGetEarliestBlockSlot(out ulong floor), Is.True);
        Assert.That(floor, Is.EqualTo(BeaconChainStore.CanonicalPruneBatchSlots));

        BeaconChainStore restarted = new(_db, Sepolia with { GloasForkEpoch = 128 });
        restarted.PruneCanonicalBlocks(epoch, anchorSlot);
        Assert.That(restarted.TryGetEarliestBlockSlot(out floor), Is.True);
        Assert.That(floor, Is.EqualTo(anchorSlot));
        Assert.That(restarted.TryGetBlockSummary(anchor, out _), Is.True);
    }

    [Test]
    public void Canonical_retention_prunes_backfill_below_the_first_anchor([Values] bool emptyPrefix)
    {
        BeaconChainSpec spec = Sepolia with { GloasForkEpoch = 128 };
        BeaconChainStore store = new(_db, spec);
        Hash256 anchor = StoreBlock(store, "anchor", 128, canonical: true);
        store.SetAnchor(anchor, 128);
        Hash256 old = StoreBlock(store, "backfill", 32, canonical: true);
        store.BackfilledBlockFloor = emptyPrefix ? 0UL : 32UL;
        Assert.That(store.TryGetEarliestBlockSlot(out ulong floor), Is.True);
        Assert.That(floor, Is.EqualTo(emptyPrefix ? 0UL : 32UL));

        BeaconChainStore restarted = new(_db, spec);
        restarted.PruneCanonicalBlocks(BeaconChainStore.MinEpochsForBlockRequests + 3, 128);
        restarted = new(_db, spec);
        restarted.BackfilledBlockFloor = 0;
        StoreBlock(restarted, "late backfill", 32, canonical: true);

        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(restarted.TryGetBlockSummary(old, out _), Is.False);
        Assert.That(restarted.TryGetCanonicalRoot(32, out _), Is.False);
        Assert.That(restarted.TryGetEarliestBlockSlot(out floor), Is.True);
        Assert.That(floor, Is.EqualTo(96UL));
        Assert.That(restarted.TryGetBlockSummary(Keccak.Compute("late backfill"), out _), Is.False);
        Assert.That(restarted.TryGetBlockSummary(anchor, out _), Is.True);
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void Stored_summary_preserves_its_fork_fields(bool gloas, bool restart)
    {
        ForkedSignedBeaconBlock block = gloas
            ? new ForkedSignedBeaconBlock.OfGloas(BlockWithBlobs())
            : new ForkedSignedBeaconBlock.OfFulu(CreateMinimalBlock(FirstGloasSlot - 1));
        Hash256 root = gloas ? GloasRoot : FuluRoot;
        _store.PutForkedBlock(root, block);
        BeaconChainStore reader = restart ? new(_db, Sepolia) : _store;
        Assert.That(reader.TryGetBlockSummary(root, out StoredBlockSummary summary), Is.True);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(summary.IsGloas, Is.EqualTo(gloas));
        Assert.That(summary.Slot, Is.EqualTo(block.Slot));
        if (gloas)
        {
            Assert.That(summary.Commitments.Select(static commitment => commitment.AsSpan().ToArray()),
                Is.EqualTo(DataColumnSidecarGloasTestFixture.Commitments().Select(static commitment => commitment.AsSpan().ToArray())));
        }
        else
        {
            Assert.That(summary.Commitments, Is.Empty);
        }
    }

    [Test]
    public void Deleted_blocks_have_no_summary([Values] bool backfillAfterDeletion)
    {
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfGloas(BlockWithBlobs());
        _store.PutForkedBlock(GloasRoot, block);
        _store.DeleteBlock(GloasRoot);
        if (backfillAfterDeletion)
        {
            _store.PutBlockSummary(GloasRoot, block);
        }
        Assert.That(_store.TryGetBlockSummary(GloasRoot, out _), Is.False);
    }

    [Test]
    public void Deletion_cannot_leave_a_summary_backfill_in_progress()
    {
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        using GatedSummaryDb db = new(_db, entered, release);
        BeaconChainStore store = new(db, Sepolia);
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfGloas(BlockWithBlobs());
        store.PutForkedBlock(GloasRoot, block);
        db.Index.Remove(SummaryKey());
        db.Index.Gate = true;
        Task backfill = Task.Run(() => store.PutBlockSummary(GloasRoot, block));
        Task? deletion = null;
        bool deletedWhileBackfilling = false;
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
            deletion = Task.Run(() => store.DeleteBlock(GloasRoot));
            deletedWhileBackfilling = deletion.Wait(TimeSpan.FromSeconds(1));
        }
        finally
        {
            release.Set();
            Assert.That(backfill.Wait(TimeSpan.FromSeconds(10)), Is.True);
            if (deletion is not null) Assert.That(deletion.Wait(TimeSpan.FromSeconds(10)), Is.True);
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(deletedWhileBackfilling, Is.False);
        Assert.That(store.TryGetBlockSummary(GloasRoot, out _), Is.False);
    }

    [Test]
    public void A_block_stored_before_the_index_has_no_summary_until_one_is_written()
    {
        SignedBeaconBlockGloas block = BlockWithBlobs();
        _store.PutForkedBlock(GloasRoot, new ForkedSignedBeaconBlock.OfGloas(block));
        MemDb index = (MemDb)_db.GetColumnDb(BeaconChainDbColumns.BlockIndex);
        index.Remove(SummaryKey());
        foreach (byte[] key in index.Keys.Where(static key => key[0] == BeaconChainStore.BlockSlotKeyPrefix).ToList())
        {
            index.Remove(key);
        }

        _store.AdvancePrunedThroughSlot(0);
        bool before = _store.TryGetBlockSummary(GloasRoot, out _);

        StoredBlockSummary written = _store.PutBlockSummary(GloasRoot, new ForkedSignedBeaconBlock.OfGloas(block));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(before, Is.False);
        Assert.That(_store.TryGetBlockSummary(GloasRoot, out StoredBlockSummary read), Is.True);
        Assert.That((read.Slot, read.IsGloas, read.Commitments.Length), Is.EqualTo((written.Slot, true, written.Commitments.Length)));
        Assert.That(written.Commitments, Is.Not.Empty);
        // A restart past a completed prune finds blocks by slot key alone, so the backfill must write one.
        Assert.That(_store.StoredBlocksForPruning(0), Does.Contain((GloasRoot, block.Message!.Slot)));
    }

    [TestCase(new byte[] { }, TestName = "An empty summary entry is not a summary")]
    [TestCase(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }, TestName = "A summary entry with no slot is not a summary")]
    [TestCase(new byte[] { 9, 0, 0, 0, 0, 0, 0, 0, 1 }, TestName = "A summary entry of an unknown shape is not a summary")]
    [TestCase(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 1, 7 }, TestName = "A summary entry with a partial commitment is not a summary")]
    public void A_malformed_summary_entry_reads_as_absent(byte[] entry)
    {
        MemDb index = (MemDb)_db.GetColumnDb(BeaconChainDbColumns.BlockIndex);
        index.Set(SummaryKey(), entry);

        Assert.That(_store.TryGetBlockSummary(GloasRoot, out _), Is.False);
    }

    [Test]
    public void A_summary_with_invalid_commitment_count_reads_as_absent([Values] bool gloas)
    {
        int count = gloas ? Eip7594DasConstants.MaxBlobCommitmentsPerBlock + 1 : 1;
        byte[] entry = new byte[9 + count * 48];
        entry[0] = gloas ? (byte)1 : (byte)0;
        _db.GetColumnDb(BeaconChainDbColumns.BlockIndex).Set(SummaryKey(), entry);

        Assert.That(_store.TryGetBlockSummary(GloasRoot, out _), Is.False);
    }

    /// <param name="prunedFromAnchor">Slot of the last completed prune relative to the anchor; null when none was recorded.</param>
    [TestCase(null, false, TestName = "Without a completed prune every slot key is read")]
    [TestCase(-6, false, TestName = "Slot keys above a completed prune are read from an unsorted index")]
    [TestCase(-6, true, TestName = "Slot keys above a completed prune are read from a sorted index")]
    [TestCase(2, true, TestName = "A prune past the persisted anchor still seeds every block above the anchor")]
    public void Startup_pruning_scan_returns_unfinalized_and_non_canonical_finalized_blocks(int? prunedFromAnchor, bool sorted)
    {
        using RecordingIndexDb db = new(sorted);
        BeaconChainStore store = new(db, Sepolia);
        (ulong anchorSlot, HashSet<(Hash256 Root, ulong Slot)> expected, _) = StoreBlocksAroundAnchor(store);
        if (prunedFromAnchor is { } offset)
        {
            ulong prunedThrough = (ulong)((long)anchorSlot + offset);
            store.AdvancePrunedThroughSlot(prunedThrough);
            // A completed prune vouches for every stored block at or below both its slot and the anchor.
            expected.RemoveWhere(block => block.Slot <= Math.Min(prunedThrough, anchorSlot));
        }

        Assert.That(store.StoredBlocksForPruning(anchorSlot), Is.EquivalentTo(expected));
    }

    [Test]
    public void Startup_pruning_scan_reads_no_summary_and_only_canonical_entries_of_finalized_blocks([Values] bool pruned)
    {
        using RecordingIndexDb db = new(sorted: true);
        BeaconChainStore store = new(db, Sepolia);
        (ulong anchorSlot, _, int finalizedSummaries) = StoreBlocksAroundAnchor(store);
        if (pruned) store.AdvancePrunedThroughSlot(anchorSlot - 6);
        db.Index.ResetRecording();
        _ = store.StoredBlocksForPruning(anchorSlot);
        // The scan runs on every restart, so only the canonical-index lookup of a finalized block may cost a read.
        Assert.That(db.Index.Reads, Has.Count.EqualTo(finalizedSummaries).And.All.Length.EqualTo(sizeof(ulong)), "summaries come with the scanned keys, not from a Get per key");
    }

    [Test]
    public void Startup_after_a_completed_prune_reads_no_key_at_or_below_its_slot()
    {
        const ulong prunedThrough = 20;
        using RecordingIndexDb db = new(sorted: true);
        BeaconChainStore store = new(db, Sepolia);
        for (ulong slot = 1; slot <= prunedThrough; slot++)
        {
            StoreBlock(store, $"finalized {slot}", slot, canonical: true);
        }

        HashSet<(Hash256, ulong)> expected =
        [
            (StoreBlock(store, "unfinalized canonical", prunedThrough + 1, canonical: true), prunedThrough + 1),
            (StoreBlock(store, "unfinalized sibling", prunedThrough + 1, canonical: false), prunedThrough + 1),
        ];
        store.AdvancePrunedThroughSlot(prunedThrough);
        db.Index.ResetRecording();

        List<(Hash256 Root, ulong Slot)> seeded = new BeaconChainStore(db, Sepolia).StoredBlocksForPruning(prunedThrough);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(seeded, Is.EquivalentTo(expected));
        // Restart cost must follow the unfinalized blocks, not the finalized history below the prune.
        Assert.That(db.Index.Scanned.Concat(db.Index.Reads), Has.All.Matches<byte[]>(key => IsBlockSlotKeyAbove(key, prunedThrough)));
    }

    private static bool IsBlockSlotKeyAbove(byte[]? key, ulong slot) =>
        key is { Length: 1 + sizeof(ulong) + Hash256.Size } && key[0] == BeaconChainStore.BlockSlotKeyPrefix
        && BinaryPrimitives.ReadUInt64BigEndian(key.AsSpan(1)) > slot;

    private static Hash256 StoreBlock(BeaconChainStore store, string name, ulong slot, bool canonical)
    {
        Hash256 root = Keccak.Compute(name);
        store.PutForkedBlock(root, new ForkedSignedBeaconBlock.OfFulu(CreateMinimalBlock(slot)));
        if (canonical) store.SetCanonicalRoot(slot, root);
        return root;
    }

    /// <returns>The anchor slot, the blocks a restart must seed for pruning, and how many stored blocks are at or below the anchor.</returns>
    private static (ulong AnchorSlot, HashSet<(Hash256 Root, ulong Slot)> Expected, int FinalizedSummaries) StoreBlocksAroundAnchor(BeaconChainStore store)
    {
        ulong anchorSlot = FirstGloasSlot - 10;
        HashSet<(Hash256 Root, ulong Slot)> expected = [];
        int finalizedSummaries = 0;
        Store("finalized canonical", anchorSlot - 5, canonical: true, seeded: false);
        Store("finalized sibling", anchorSlot - 5, canonical: false, seeded: true);
        Store("finalized without canonical entry", anchorSlot - 3, canonical: false, seeded: true);
        Store("anchor", anchorSlot, canonical: true, seeded: false);
        Store("unfinalized canonical", anchorSlot + 2, canonical: true, seeded: true);
        Store("unfinalized sibling", anchorSlot + 2, canonical: false, seeded: true);
        // A finalization delete loop interrupted between two non-canonical blocks of the same slot.
        Hash256 deleted = Store("deleted before interruption", anchorSlot - 4, canonical: false, seeded: false);
        store.DeleteBlock(deleted);
        finalizedSummaries--;
        Store("left by interruption", anchorSlot - 4, canonical: false, seeded: true);
        return (anchorSlot, expected, finalizedSummaries);

        Hash256 Store(string name, ulong slot, bool canonical, bool seeded)
        {
            Hash256 root = StoreBlock(store, name, slot, canonical);
            if (seeded) expected.Add((root, slot));
            if (slot <= anchorSlot) finalizedSummaries++;
            return root;
        }
    }

    private static byte[] SummaryKey() => new[] { BeaconChainStore.BlockSummaryKeyPrefix }.Concat(GloasRoot.Bytes.ToArray()).ToArray();

    private static SignedBeaconBlockGloas BlockWithBlobs()
    {
        SignedBeaconBlockGloas block = CreateMinimalGloasBlock(FirstGloasSlot + 1);
        block.Message!.Body!.SignedExecutionPayloadBid!.Message!.BlobKzgCommitments = DataColumnSidecarGloasTestFixture.Commitments();
        return block;
    }

    private sealed class GatedSummaryDb(MemColumnsDb<BeaconChainDbColumns> inner, ManualResetEventSlim entered, ManualResetEventSlim release) : TestColumnsDb
    {
        public GatedSummaryIndex Index { get; } = new(entered, release);
        protected override IDb CreateColumn(BeaconChainDbColumns key) => key == BeaconChainDbColumns.BlockIndex ? Index : inner.GetColumnDb(key);
        public override void Dispose() => Index.Dispose();
    }

    private sealed class GatedSummaryIndex(ManualResetEventSlim entered, ManualResetEventSlim release) : MemDb
    {
        public bool Gate { get; set; }

        public override void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (Gate && key.Length == 1 + Hash256.Size && key[0] == BeaconChainStore.BlockSummaryKeyPrefix && value is not null)
            {
                Gate = false;
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Summary write was never released");
            }

            base.Set(key, value, flags);
        }
    }

    // A sorted index seeks like the on-disk column; an unsorted one must walk its keys.
    private sealed class RecordingIndexDb(bool sorted) : TestColumnsDb
    {
        public RecordingIndex Index { get; } = sorted ? new SortedRecordingIndex() : new RecordingIndex();
        protected override IDb CreateColumn(BeaconChainDbColumns key) => key == BeaconChainDbColumns.BlockIndex ? Index : base.CreateColumn(key);
    }

    private class RecordingIndex : MemDb, IDb
    {
        public List<byte[]> Reads { get; } = [];
        public List<byte[]> Scanned { get; } = [];

        public void ResetRecording()
        {
            Reads.Clear();
            Scanned.Clear();
        }

        public override byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            Reads.Add(key.ToArray());
            return base.Get(key, flags);
        }

        public new IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false)
        {
            foreach (KeyValuePair<byte[], byte[]> pair in base.GetAll(ordered))
            {
                Scanned.Add(pair.Key);
                yield return pair;
            }
        }

        public new IEnumerable<byte[]> GetAllKeys(bool ordered = false)
        {
            foreach (byte[] key in base.GetAllKeys(ordered))
            {
                Scanned.Add(key);
                yield return key;
            }
        }
    }

    private sealed class SortedRecordingIndex : RecordingIndex, ISortedKeyValueStore
    {
        public byte[]? FirstKey => Keys.MinBy(static key => key, Bytes.Comparer);
        public byte[]? LastKey => Keys.MaxBy(static key => key, Bytes.Comparer);

        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None)
        {
            byte[] first = firstKeyInclusive.ToArray();
            byte[] last = lastKeyExclusive.ToArray();
            List<KeyValuePair<byte[], byte[]>> inRange = [.. ((MemDb)this).GetAll(ordered: true)
                .Where(pair => Bytes.BytesComparer.Compare(pair.Key, first) >= 0 && Bytes.BytesComparer.Compare(pair.Key, last) < 0)];
            return new RecordingView(this, inRange);
        }

        private sealed class RecordingView(SortedRecordingIndex owner, List<KeyValuePair<byte[], byte[]>> entries) : ISortedView
        {
            private int _index = -1;

            public bool StartBefore(ReadOnlySpan<byte> value) => throw new NotSupportedException();

            public bool MoveNext()
            {
                if (++_index >= entries.Count) return false;
                owner.Scanned.Add(entries[_index].Key);
                return true;
            }

            public ReadOnlySpan<byte> CurrentKey => entries[_index].Key;
            public ReadOnlySpan<byte> CurrentValue => entries[_index].Value;
            public void Dispose() { }
        }
    }
}
