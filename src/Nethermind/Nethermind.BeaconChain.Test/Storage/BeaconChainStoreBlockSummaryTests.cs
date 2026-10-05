// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
        bool before = _store.TryGetBlockSummary(GloasRoot, out _);

        StoredBlockSummary written = _store.PutBlockSummary(GloasRoot, new ForkedSignedBeaconBlock.OfGloas(block));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(before, Is.False);
        Assert.That(_store.TryGetBlockSummary(GloasRoot, out StoredBlockSummary read), Is.True);
        Assert.That((read.Slot, read.IsGloas, read.Commitments.Length), Is.EqualTo((written.Slot, true, written.Commitments.Length)));
        Assert.That(written.Commitments, Is.Not.Empty);
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
}
