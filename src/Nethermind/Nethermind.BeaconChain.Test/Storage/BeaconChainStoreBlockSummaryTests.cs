// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.Storage;

/// <summary>Checks durable block summaries used by gloas/p2p-interface.md sidecar validation.</summary>
public class BeaconChainStoreBlockSummaryTests
{
    private static readonly Hash256 GloasRoot = Keccak.Compute("gloas block");
    private static readonly Hash256 FuluRoot = Keccak.Compute("fulu block");

    private MemColumnsDb<BeaconChainDbColumns> _db = null!;
    private BeaconChainStore _store = null!;

    /// <summary>Creates an isolated database for gloas/p2p-interface.md summary checks.</summary>
    [SetUp]
    public void CreateStore()
    {
        _db = new MemColumnsDb<BeaconChainDbColumns>();
        _store = new BeaconChainStore(_db, Sepolia);
    }

    /// <summary>Releases the database used by gloas/p2p-interface.md summary checks.</summary>
    [TearDown]
    public void DisposeStore() => _db.Dispose();

    /// <summary>Checks that the stored fields match the block used by gloas/p2p-interface.md validation.</summary>
    [Test]
    public void A_stored_gloas_block_has_a_summary_of_its_slot_and_bid_commitments()
    {
        SignedBeaconBlockGloas block = BlockWithBlobs();
        _store.PutForkedBlock(GloasRoot, new ForkedSignedBeaconBlock.OfGloas(block));

        bool found = _store.TryGetBlockSummary(GloasRoot, out StoredBlockSummary summary);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(found, Is.True);
        Assert.That(summary.IsGloas, Is.True);
        Assert.That(summary.Slot, Is.EqualTo(block.Message!.Slot));
        Assert.That(summary.Commitments.Select(static c => c.AsSpan().ToArray()),
            Is.EqualTo(DataColumnSidecarGloasTestFixture.Commitments().Select(static c => c.AsSpan().ToArray())));
    }

    /// <summary>Checks that a pre-Gloas summary cannot supply a bid for gloas/p2p-interface.md validation.</summary>
    [Test]
    public void A_stored_fulu_block_has_a_summary_that_is_not_gloas()
    {
        SignedBeaconBlock block = CreateMinimalBlock(FirstGloasSlot - 1);
        _store.PutForkedBlock(FuluRoot, new ForkedSignedBeaconBlock.OfFulu(block));

        bool found = _store.TryGetBlockSummary(FuluRoot, out StoredBlockSummary summary);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(found, Is.True);
        Assert.That(summary.IsGloas, Is.False);
        Assert.That(summary.Slot, Is.EqualTo(FirstGloasSlot - 1));
        Assert.That(summary.Commitments, Is.Empty);
    }

    /// <summary>Checks that gloas/p2p-interface.md validation retains its block fields across restarts.</summary>
    [Test]
    public void The_summary_survives_a_restart()
    {
        SignedBeaconBlockGloas block = BlockWithBlobs();
        _store.PutForkedBlock(GloasRoot, new ForkedSignedBeaconBlock.OfGloas(block));

        BeaconChainStore reopened = new(_db, Sepolia);

        Assert.That(reopened.TryGetBlockSummary(GloasRoot, out StoredBlockSummary summary), Is.True);
        Assert.That((summary.Slot, summary.Commitments.Length), Is.EqualTo((block.Message!.Slot, DataColumnSidecarGloasTestFixture.Commitments().Length)));
    }

    /// <summary>Checks that deletion removes the block fields used by gloas/p2p-interface.md validation.</summary>
    [Test]
    public void Deleting_a_block_deletes_its_summary()
    {
        _store.PutForkedBlock(GloasRoot, new ForkedSignedBeaconBlock.OfGloas(BlockWithBlobs()));

        _store.DeleteBlock(GloasRoot);

        Assert.That(_store.TryGetBlockSummary(GloasRoot, out _), Is.False);
    }

    /// <summary>Checks that a late backfill cannot resurrect a block summary for gloas/p2p-interface.md validation.</summary>
    [Test]
    public void Backfilling_a_deleted_block_does_not_restore_its_summary()
    {
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfGloas(BlockWithBlobs());
        _store.PutForkedBlock(GloasRoot, block);
        _store.DeleteBlock(GloasRoot);

        _store.PutBlockSummary(GloasRoot, block);

        Assert.That(_store.TryGetBlockSummary(GloasRoot, out _), Is.False);
    }

    /// <summary>Checks that concurrent deletion cannot leave stale block fields for gloas/p2p-interface.md validation.</summary>
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

    /// <summary>Checks that legacy block fields can be persisted for subsequent gloas/p2p-interface.md validation.</summary>
    [Test]
    public void A_block_stored_before_the_index_has_no_summary_until_one_is_written()
    {
        SignedBeaconBlockGloas block = BlockWithBlobs();
        _store.PutForkedBlock(GloasRoot, new ForkedSignedBeaconBlock.OfGloas(block));
        MemDb index = (MemDb)_db.GetColumnDb(BeaconChainDbColumns.BlockIndex);
        index.Remove(SummaryKey());
        bool before = _store.TryGetBlockSummary(GloasRoot, out _);

        StoredBlockSummary written = _store.PutBlockSummary(GloasRoot, new ForkedSignedBeaconBlock.OfGloas(block));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(before, Is.False);
        Assert.That(_store.TryGetBlockSummary(GloasRoot, out StoredBlockSummary read), Is.True);
        Assert.That((read.Slot, read.IsGloas, read.Commitments.Length), Is.EqualTo((written.Slot, true, written.Commitments.Length)));
        Assert.That(written.Commitments, Is.Not.Empty);
    }

    /// <summary>Checks that malformed records cannot supply fields for gloas/p2p-interface.md validation.</summary>
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

    /// <summary>Checks that invalid commitment counts cannot supply a bid for gloas/p2p-interface.md validation.</summary>
    [Test]
    public void A_summary_with_invalid_commitment_count_reads_as_absent([Values] bool gloas)
    {
        int count = gloas ? Eip7594DasConstants.MaxBlobCommitmentsPerBlock + 1 : 1;
        byte[] entry = new byte[9 + count * 48];
        entry[0] = gloas ? (byte)1 : (byte)0;
        _db.GetColumnDb(BeaconChainDbColumns.BlockIndex).Set(SummaryKey(), entry);

        Assert.That(_store.TryGetBlockSummary(GloasRoot, out _), Is.False);
    }

    private static byte[] SummaryKey() => new[] { BeaconChainStore.BlockSummaryKeyPrefix }.Concat(GloasRoot.Bytes.ToArray()).ToArray();

    private static SignedBeaconBlockGloas BlockWithBlobs()
    {
        SignedBeaconBlockGloas block = CreateMinimalGloasBlock(FirstGloasSlot + 1);
        block.Message!.Body!.SignedExecutionPayloadBid!.Message!.BlobKzgCommitments = DataColumnSidecarGloasTestFixture.Commitments();
        return block;
    }

    private sealed class GatedSummaryDb(MemColumnsDb<BeaconChainDbColumns> inner, ManualResetEventSlim entered, ManualResetEventSlim release) : IColumnsDb<BeaconChainDbColumns>
    {
        public GatedSummaryIndex Index { get; } = new(entered, release);
        public IDb GetColumnDb(BeaconChainDbColumns key) => key == BeaconChainDbColumns.BlockIndex ? Index : inner.GetColumnDb(key);
        public IEnumerable<BeaconChainDbColumns> ColumnKeys => inner.ColumnKeys;
        public IColumnsWriteBatch<BeaconChainDbColumns> StartWriteBatch() => new InMemoryColumnWriteBatch<BeaconChainDbColumns>(this);
        public IColumnDbSnapshot<BeaconChainDbColumns> CreateSnapshot() => throw new NotSupportedException();
        public void Flush(bool onlyWal = false) { }
        public void Dispose() => Index.Dispose();
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
}
