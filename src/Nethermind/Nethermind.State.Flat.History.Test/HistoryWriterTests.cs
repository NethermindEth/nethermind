// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Test;
using Nethermind.Trie.Pruning;
using System.IO;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

[TestFixture]
public class HistoryWriterTests
{
    private const bool RlpWrapSlots = true;

    private static readonly Address AddrA = TestItem.AddressA;
    private static readonly Address AddrB = TestItem.AddressB;
    private static readonly UInt256 Slot1 = 1;
    private static readonly UInt256 Slot2 = 2;

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;
    private ResourcePool _resourcePool = null!;
    private FlatTestContainer _tier = null!;
    private SnapshotRepository _repository = null!;
    private HistoryWriter _writer = null!;
    private HistoryReader _reader = null!;
    private HistoryAvailability _availability = null!;
    private HistoryRowFormat _rowFormat = null!;
    private HistoryStore _accountHistory = null!;
    private HistoryStore _storageHistory = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _resourcePool = new ResourcePool(new FlatDbConfig { CompactSize = 16 });
        _tier = new FlatTestContainer(new FlatDbConfig { CompactSize = 16 });
        _repository = _tier.Repository;
        FlatDbConfig config = new() { HistoryEnabled = true };
        (_availability, _rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        _writer = new HistoryWriter(_db, _historyColumns, config, _availability, _rowFormat, LimboLogs.Instance);
        _reader = new HistoryReader(_db, _historyColumns, _availability, _rowFormat, LimboLogs.Instance);
        _accountHistory = new HistoryStore(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory), LimboLogs.Instance.GetClassLogger<HistoryStore>());
        _storageHistory = new HistoryStore(_historyColumns.GetColumnDb(FlatHistoryColumns.StorageHistory), LimboLogs.Instance.GetClassLogger<HistoryStore>());
    }

    [TearDown]
    public void TearDown()
    {
        _tier.Dispose();
        _db.Dispose();
        _historyColumns.Dispose();
    }

    [TestCase(0ul, 0ul, ExpectedKind.Absent)]   // before the first change -> absent at that height
    [TestCase(1ul, 100ul, ExpectedKind.Value)]
    [TestCase(2ul, 200ul, ExpectedKind.Value)]
    [TestCase(3ul, 0ul, ExpectedKind.Tombstone)] // deleted
    [TestCase(4ul, 0ul, ExpectedKind.Tombstone)]
    public void Captures_account_value_as_of_block(ulong readBlock, ulong balance, ExpectedKind kind)
    {
        SeedGenesisFloor();
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 100))]);
        CommitBlock(1, 2, accountChanges: [(AddrA, new Account(2, 200))]);
        CommitBlock(2, 3, accountChanges: [(AddrA, null)]);

        _writer.CaptureUpTo(StateAt(3), _repository, CancellationToken.None);

        ReadOnlySpan<byte> flatKey = AccountKey(AddrA);
        Span<byte> buffer = stackalloc byte[256];
        int written = _accountHistory.TryGetAt(readBlock, flatKey, buffer);

        using (Assert.EnterMultipleScope())
        {
            switch (kind)
            {
                case ExpectedKind.Absent:
                    Assert.That(written, Is.EqualTo(-1));
                    break;
                case ExpectedKind.Tombstone:
                    Assert.That(written, Is.EqualTo(0));
                    break;
                default:
                    Assert.That(buffer[..written].ToArray(), Is.EqualTo(EncodedAccount(new Account(readBlock, balance))));
                    break;
            }
        }
    }

    // A walk records a key's rows in descending block order: visiting a block writes the row for the higher block
    // seen before it, and the lowest row is only resolved once the walk connects. Commit per block and a key's upper
    // row is visible while its lower row does not exist, so a read below the watermark seeks upward, finds the upper
    // row, and answers with a value from after the block it asked about. One batch for the walk forbids that, and
    // since nothing partial is ever observable there is no half-written state to assert on instead - the count is it.
    [Test]
    public void A_capture_walk_publishes_every_row_in_one_batch()
    {
        BatchCountingHistoryColumns counting = new(_historyColumns);
        HistoryWriter writer = new(_db, counting, new FlatDbConfig { HistoryEnabled = true }, _availability, _rowFormat, LimboLogs.Instance);

        // The same key at more than one block of the walk is the shape that goes wrong per batch.
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 100))]);
        CommitBlock(1, 2, accountChanges: [(AddrA, new Account(2, 200))]);
        CommitBlock(2, 3, accountChanges: [(AddrA, new Account(3, 300))]);

        writer.CaptureUpTo(StateAt(3), _repository, CancellationToken.None);

        Assert.That(counting.BatchesStarted, Is.EqualTo(1));
    }

    private sealed class BatchCountingHistoryColumns(IColumnsDb<FlatHistoryColumns> inner) : IColumnsDb<FlatHistoryColumns>
    {
        public int BatchesStarted { get; private set; }

        public IColumnsWriteBatch<FlatHistoryColumns> StartWriteBatch()
        {
            BatchesStarted++;
            return inner.StartWriteBatch();
        }

        public IDb GetColumnDb(FlatHistoryColumns key) => inner.GetColumnDb(key);
        public IEnumerable<FlatHistoryColumns> ColumnKeys => inner.ColumnKeys;
        public IColumnDbSnapshot<FlatHistoryColumns> CreateSnapshot() => inner.CreateSnapshot();
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);
        public void SyncWal() => inner.SyncWal();

        // The fixture owns the wrapped database.
        public void Dispose() { }
    }

    [Test]
    public void A_throw_while_resolving_the_pending_rows_publishes_nothing()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetentionBlocks = 100 };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        ThrowingHistoryColumns throwing = new(_historyColumns, throwOnAccountWrite: 3);
        HistoryWriter writer = new(_db, throwing, config, availability, rowFormat, LimboLogs.Instance);
        writer.SeedGenesis([], StateAt(0).StateRoot);

        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 100))]);
        CommitBlock(1, 2, accountChanges: [(AddrA, new Account(2, 200))]);
        CommitBlock(2, 3, accountChanges: [(AddrA, new Account(3, 300))]);

        Assert.Throws<InvalidOperationException>(() => writer.CaptureUpTo(StateAt(3), _repository, CancellationToken.None));

        HistoryStoreV3 accountHistory = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory));
        Span<byte> buffer = stackalloc byte[256];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(throwing.AccountWrites, Is.EqualTo(3),
                "the walk has to have written its two rows before the throw, or this exercises the walk rather than the resolve");
            Assert.That(accountHistory.TryGetValueBeforeNextChange(0, AccountKey(AddrA), buffer, out _), Is.EqualTo(-1));
            Assert.That(accountHistory.TryGetValueBeforeNextChange(1, AccountKey(AddrA), buffer, out _), Is.EqualTo(-1));
            Assert.That(accountHistory.TryGetValueBeforeNextChange(2, AccountKey(AddrA), buffer, out _), Is.EqualTo(-1));
            Assert.That(writer.LastCapturedBlock, Is.EqualTo(0UL));
        }
    }

    private sealed class ThrowingHistoryColumns(IColumnsDb<FlatHistoryColumns> inner, int throwOnAccountWrite) : IColumnsDb<FlatHistoryColumns>
    {
        public int AccountWrites { get; private set; }

        public IColumnsWriteBatch<FlatHistoryColumns> StartWriteBatch() => new Batch(this, inner.StartWriteBatch());

        public IDb GetColumnDb(FlatHistoryColumns key) => inner.GetColumnDb(key);
        public IEnumerable<FlatHistoryColumns> ColumnKeys => inner.ColumnKeys;
        public IColumnDbSnapshot<FlatHistoryColumns> CreateSnapshot() => inner.CreateSnapshot();
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);
        public void SyncWal() => inner.SyncWal();
        public void Dispose() { }

        private void CountAccountWrite()
        {
            if (++AccountWrites == throwOnAccountWrite) throw new InvalidOperationException("account history write failed");
        }

        private sealed class Batch(ThrowingHistoryColumns owner, IColumnsWriteBatch<FlatHistoryColumns> inner) : IColumnsWriteBatch<FlatHistoryColumns>
        {
            public IWriteBatch GetColumnBatch(FlatHistoryColumns key) => key == FlatHistoryColumns.AccountHistory
                ? new CountingBatch(owner, inner.GetColumnBatch(key))
                : inner.GetColumnBatch(key);

            public void Clear() => inner.Clear();
            public void Dispose() => inner.Dispose();
        }

        private sealed class CountingBatch(ThrowingHistoryColumns owner, IWriteBatch inner) : IWriteBatch
        {
            public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
            {
                owner.CountAccountWrite();
                inner.Set(key, value, flags);
            }

            public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
            {
                owner.CountAccountWrite();
                inner.PutSpan(key, value, flags);
            }

            public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => inner.Merge(key, value, flags);
            public void Clear() => inner.Clear();
            public void Dispose() => inner.Dispose();
        }
    }

    [TestCase(0ul, null)]
    [TestCase(1ul, "0a")]
    [TestCase(2ul, "0bbb")]
    [TestCase(3ul, "", true)]
    [TestCase(4ul, "", true)]
    public void Captures_storage_value_as_of_block(ulong readBlock, string? expectedHex, bool expectTombstone = false)
    {
        SeedGenesisFloor();
        CommitBlock(0, 1, storageChanges: [(AddrA, Slot1, Slot(0x0a))]);
        CommitBlock(1, 2, storageChanges: [(AddrA, Slot1, Slot(0x0b, 0xbb))]);
        CommitBlock(2, 3, storageChanges: [(AddrA, Slot1, null)]);

        _writer.CaptureUpTo(StateAt(3), _repository, CancellationToken.None);

        ReadOnlySpan<byte> flatKey = StorageKey(AddrA, Slot1);
        Span<byte> buffer = stackalloc byte[64];
        int written = _storageHistory.TryGetAt(readBlock, flatKey, buffer);

        using (Assert.EnterMultipleScope())
        {
            if (expectedHex is null)
            {
                Assert.That(written, Is.EqualTo(-1));
            }
            else if (expectTombstone)
            {
                Assert.That(written, Is.EqualTo(0));
            }
            else
            {
                Assert.That(written, Is.GreaterThan(0));
                Assert.That(buffer[..written].ToArray(), Is.EqualTo(EncodedSlot(Convert.FromHexString(expectedHex))));
            }
        }
    }

    [Test]
    public void Recorded_bytes_match_the_flat_encoders()
    {
        SeedGenesisFloor();
        Account account = new(7, 4242);
        SlotValue slot = Slot(0xde, 0xad, 0xbe, 0xef);
        CommitBlock(0, 1, accountChanges: [(AddrB, account)], storageChanges: [(AddrB, Slot2, slot)]);

        _writer.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        Span<byte> buffer = stackalloc byte[256];
        int accountWritten = _accountHistory.TryGetAt(1, AccountKey(AddrB), buffer);
        byte[] accountBytes = buffer[..accountWritten].ToArray();

        int slotWritten = _storageHistory.TryGetAt(1, StorageKey(AddrB, Slot2), buffer);
        byte[] slotBytes = buffer[..slotWritten].ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountBytes, Is.EqualTo(EncodedAccount(account)));
            Assert.That(slotBytes, Is.EqualTo(EncodedSlot(slot.AsReadOnlySpan)));
        }
    }

    [Test]
    public void CaptureUpTo_is_resumable_and_skips_already_captured_blocks()
    {
        SeedGenesisFloor();
        Account atBlock1 = new(1, 11);
        Account atBlock2 = new(2, 22);
        CommitBlock(0, 1, accountChanges: [(AddrA, atBlock1)]);
        _writer.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        CommitBlock(1, 2, accountChanges: [(AddrA, atBlock2)]);
        _writer.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        Span<byte> buffer = stackalloc byte[256];
        ReadOnlySpan<byte> flatKey = AccountKey(AddrA);
        byte[] read1 = buffer[.._accountHistory.TryGetAt(1, flatKey, buffer)].ToArray();
        byte[] read2 = buffer[.._accountHistory.TryGetAt(2, flatKey, buffer)].ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_writer.LastCapturedBlock, Is.EqualTo(2));
            Assert.That(read1, Is.EqualTo(EncodedAccount(atBlock1)));
            Assert.That(read2, Is.EqualTo(EncodedAccount(atBlock2)));
        }
    }

    [TestCase(0ul, 0ul)]   // before any change -> absent
    [TestCase(1ul, 100ul)] // created
    [TestCase(2ul, 0ul)]   // self-destructed -> absent
    [TestCase(3ul, 300ul)] // re-created
    [TestCase(4ul, 300ul)] // still present afterwards
    public void Account_selfdestruct_then_recreate_reads_per_height(ulong readBlock, ulong expectedBalance)
    {
        SeedGenesisFloor();
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 100))]);
        CommitBlock(1, 2, accountChanges: [(AddrA, null)]);
        CommitBlock(2, 3, accountChanges: [(AddrA, new Account(3, 300))]);

        _writer.CaptureUpTo(StateAt(3), _repository, CancellationToken.None);

        bool found = _reader.TryGetAccount(readBlock, AddrA, out AccountStruct account);

        using (Assert.EnterMultipleScope())
        {
            if (expectedBalance == 0)
            {
                Assert.That(found, Is.False);
            }
            else
            {
                Assert.That(found, Is.True);
                Assert.That(account.Balance, Is.EqualTo((UInt256)expectedBalance));
            }
        }
    }

    [TestCase(0ul, null, false, false)]
    [TestCase(1ul, "0a", false, false)]
    [TestCase(2ul, null, false, false)]
    [TestCase(3ul, "0c", false, false)]
    [TestCase(4ul, "0c", false, false)]
    [TestCase(0ul, null, true, false)]
    [TestCase(1ul, "0a", true, false)]
    [TestCase(2ul, null, true, false)]
    [TestCase(3ul, "0c", true, false)]
    [TestCase(4ul, "0c", true, false)]
    [TestCase(0ul, null, true, true)]
    [TestCase(1ul, "0a", true, true)]
    [TestCase(2ul, null, true, true)]
    [TestCase(3ul, "0c", true, true)]
    [TestCase(4ul, "0c", true, true)]
    public void Storage_killed_then_rewritten_reads_per_height(ulong readBlock, string? expectedHex, bool viaSelfDestruct, bool killBlockConverted)
    {
        SeedGenesisFloor();
        CommitBlock(0, 1, storageChanges: [(AddrA, Slot1, HistorySlot(0x0a))]);
        if (viaSelfDestruct)
            CommitBlock(1, 2, accountChanges: [(AddrA, null)], selfDestructs: [(AddrA, false)]);
        else
            CommitBlock(1, 2, storageChanges: [(AddrA, Slot1, null)]);
        CommitBlock(2, 3, storageChanges: [(AddrA, Slot1, HistorySlot(0x0c))]);
        if (killBlockConverted) ConvertToPersistedTier(2);

        _writer.CaptureUpTo(StateAt(3), _repository, CancellationToken.None);

        AssertStorageAt(readBlock, Slot1, expectedHex);
    }

    [Test]
    public void Storage_untouched_after_selfdestruct_reads_empty_while_rewritten_slot_reads_new_value()
    {
        SeedGenesisFloor();
        CommitBlock(0, 1, storageChanges: [(AddrA, Slot1, HistorySlot(0x0a)), (AddrA, Slot2, HistorySlot(0x0b))]);
        CommitBlock(1, 2, accountChanges: [(AddrA, null)], selfDestructs: [(AddrA, false)]);
        CommitBlock(2, 3, accountChanges: [(AddrA, new Account(1, 100))], storageChanges: [(AddrA, Slot1, HistorySlot(0x0c))]);

        _writer.CaptureUpTo(StateAt(3), _repository, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            AssertStorageAt(3, Slot1, "0c");
            AssertStorageAt(3, Slot2, null); // never re-written after the destruct -> dead
        }
    }

    [Test]
    public void Storage_destructed_and_rewritten_in_same_block_reads_the_rewrite()
    {
        SeedGenesisFloor();
        CommitBlock(0, 1, storageChanges: [(AddrA, Slot1, HistorySlot(0x0a))]);
        CommitBlock(1, 2, storageChanges: [(AddrA, Slot1, HistorySlot(0x0b))], selfDestructs: [(AddrA, false)]);

        _writer.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            AssertStorageAt(1, Slot1, "0a");
            AssertStorageAt(2, Slot1, "0b");
            AssertStorageAt(3, Slot1, "0b");
        }
    }

    [Test]
    public void Selfdestruct_of_account_without_persisted_storage_records_no_clear()
    {
        SeedGenesisFloor();
        CommitBlock(0, 1, storageChanges: [(AddrA, Slot1, HistorySlot(0x0a))]);
        CommitBlock(1, 2, selfDestructs: [(AddrA, true)]);

        _writer.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        AssertStorageAt(3, Slot1, "0a");
    }

    [Test]
    public void Empty_account_round_trips_as_present_not_tombstone()
    {
        SeedGenesisFloor();
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(0UL, UInt256.Zero))]);

        _writer.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        bool found = _reader.TryGetAccount(1, AddrA, out AccountStruct account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(account.Nonce, Is.EqualTo(0UL));
            Assert.That(account.Balance, Is.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public void Genesis_allocations_are_captured_and_readable_at_later_blocks()
    {
        CommitGenesis(
            accountChanges: [(AddrA, new Account(0UL, 1000))],
            storageChanges: [(AddrA, Slot1, HistorySlot(0x0a))]);
        CommitBlock(0, 1, accountChanges: [(AddrB, new Account(1, 1))]);
        CommitBlock(1, 2, accountChanges: [(AddrB, new Account(2, 2))]);

        _writer.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        bool atGenesis = _reader.TryGetAccount(0, AddrA, out AccountStruct genesisAccount);
        bool atLater = _reader.TryGetAccount(2, AddrA, out AccountStruct laterAccount);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_reader.HasHistoryForBlock(0), Is.True);
            Assert.That(atGenesis, Is.True);
            Assert.That(genesisAccount.Balance, Is.EqualTo((UInt256)1000));
            Assert.That(atLater, Is.True, "a genesis allocation never touched again must resolve at a later block");
            Assert.That(laterAccount.Balance, Is.EqualTo((UInt256)1000));
            AssertStorageAt(2, Slot1, "0a");
        }
    }

    [Test]
    public void Capture_that_cannot_connect_leaves_watermark_unadvanced()
    {
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 1))]);
        CommitBlock(1, 2, accountChanges: [(AddrA, new Account(2, 2))]);

        _writer.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_writer.LastCapturedBlock, Is.EqualTo(0UL));
            Assert.That(_reader.HasHistoryForBlock(1), Is.False);
            Assert.That(_reader.HasHistoryForBlock(2), Is.False);
        }
    }

    [Test]
    public void Recapture_after_restart_is_idempotent_and_extends_from_watermark()
    {
        SeedGenesisFloor();
        Account atBlock1 = new(1, 11);
        Account atBlock2 = new(2, 22);
        CommitBlock(0, 1, accountChanges: [(AddrA, atBlock1)]);
        CommitBlock(1, 2, accountChanges: [(AddrA, atBlock2)]);
        _writer.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        HistoryWriter restarted = new(_db, _historyColumns, new FlatDbConfig { HistoryEnabled = true }, _availability, _rowFormat, LimboLogs.Instance);
        restarted.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        Account atBlock3 = new(3, 33);
        CommitBlock(2, 3, accountChanges: [(AddrA, atBlock3)]);
        restarted.CaptureUpTo(StateAt(3), _repository, CancellationToken.None);

        Span<byte> buffer = stackalloc byte[256];
        ReadOnlySpan<byte> flatKey = AccountKey(AddrA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(restarted.LastCapturedBlock, Is.EqualTo(3));
            Assert.That(buffer[.._accountHistory.TryGetAt(1, flatKey, buffer)].ToArray(), Is.EqualTo(EncodedAccount(atBlock1)));
            Assert.That(buffer[.._accountHistory.TryGetAt(2, flatKey, buffer)].ToArray(), Is.EqualTo(EncodedAccount(atBlock2)));
            Assert.That(buffer[.._accountHistory.TryGetAt(3, flatKey, buffer)].ToArray(), Is.EqualTo(EncodedAccount(atBlock3)));
        }
    }

    // A walk that does not connect must leave the column as it found it. Publishing what it accumulated is the row
    // set the single batch exists to prevent: a key's upper row is written while visiting a lower block, its lowest
    // row only once the walk connects, so half a walk answers reads below the watermark with a value from after the
    // block they asked about - and the refusal paths disable capture, so nothing would ever come back to correct it.
    [Test]
    public void An_unconnected_walk_publishes_nothing()
    {
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 1))]);
        CommitBlock(1, 2, accountChanges: [(AddrA, new Account(2, 2))]);

        // No seed, so the walk can reach neither a watermark nor PreGenesis and refuses to connect.
        _writer.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        Span<byte> buffer = stackalloc byte[256];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_writer.LastCapturedBlock, Is.EqualTo(0UL));
            Assert.That(_accountHistory.TryGetAt(1, AccountKey(AddrA), buffer), Is.EqualTo(-1));
            Assert.That(_accountHistory.TryGetAt(2, AccountKey(AddrA), buffer), Is.EqualTo(-1));
            Assert.DoesNotThrow(() => _ = new HistoryReader(_db, _historyColumns, _availability, _rowFormat, LimboLogs.Instance));
        }
    }

    [Test]
    public void Capture_health_requires_a_proven_capture()
    {
        Assert.That(_writer.CaptureHealthy, Is.False, "no capture has run yet");

        SeedGenesisFloor();
        Assert.That(_writer.CaptureHealthy, Is.False, "the seed alone must not report health");

        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 1))]);
        _writer.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        Assert.That(_writer.CaptureHealthy, Is.True, "a completed capture proves the pipeline runs");
    }

    [Test]
    public void Reorged_capture_at_the_connect_point_refuses_to_advance_the_watermark()
    {
        SeedGenesisFloor();
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 11))]);
        _writer.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);
        Assert.That(_writer.LastCapturedBlock, Is.EqualTo(1UL));

        Span<byte> reorgedRoot = stackalloc byte[32];
        reorgedRoot[0] = 0xde;
        reorgedRoot[1] = 0xad;
        StateId reorgedParent = new(1, new ValueHash256(reorgedRoot));
        Snapshot snapshot = _resourcePool.CreateSnapshot(reorgedParent, StateAt(2), ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot.Content.Accounts[AddrB] = new Account(2, 22);
        Assert.That(_repository.TryAdd(snapshot, SnapshotTier.InMemoryBase), Is.True);
        _repository.AddStateId(StateAt(2));

        _writer.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_writer.LastCapturedBlock, Is.EqualTo(1UL), "the watermark must not advance over a reorged capture");
            Assert.That(_reader.HasHistoryForBlock(2), Is.False);
            Assert.That(_writer.CaptureHealthy, Is.False, "capture must self-disable so dependants stop relying on it");
        }
    }

    [Test]
    public void Repeated_capture_failures_trip_the_breaker_and_let_persistence_resume()
    {
        SeedGenesisFloor();
        ISnapshotRepository failing = Substitute.For<ISnapshotRepository>();
        failing.TryLeaseInMemoryState(default, default, out _).ThrowsForAnyArgs(new IOException("disk failure"));

        for (int i = 0; i < 16; i++)
        {
            Assert.Throws<IOException>(() => _writer.CaptureUpTo(StateAt(2), failing, CancellationToken.None),
                $"failure {i + 1} must still abort the persist for a retry");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.DoesNotThrow(() => _writer.CaptureUpTo(StateAt(3), failing, CancellationToken.None),
                "after the breaker trips capture must be skipped so persistence can resume");
            Assert.That(_writer.CaptureHealthy, Is.False);
            Assert.That(_writer.LastCapturedBlock, Is.EqualTo(0UL), "reads above the frozen watermark fail closed");
        }
    }

    [Test]
    public void Successful_capture_raises_watermark_advanced()
    {
        SeedGenesisFloor();
        ulong advancedTo = 0;
        _writer.WatermarkAdvanced += w => advancedTo = w;

        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 1))]);
        _writer.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        Assert.That(advancedTo, Is.EqualTo(1UL));
    }

    [Test]
    public void Watermark_advanced_handler_failure_is_contained()
    {
        SeedGenesisFloor();
        _writer.WatermarkAdvanced += _ => throw new InvalidOperationException("handler failure");

        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 1))]);

        Assert.DoesNotThrow(() => _writer.CaptureUpTo(StateAt(1), _repository, CancellationToken.None));
        Assert.That(_writer.LastCapturedBlock, Is.EqualTo(1UL));
    }

    [Test]
    public void Breaker_trip_raises_capture_disabled_once()
    {
        SeedGenesisFloor();
        ISnapshotRepository failing = Substitute.For<ISnapshotRepository>();
        failing.TryLeaseInMemoryState(default, default, out _).ThrowsForAnyArgs(new IOException("disk failure"));
        int disabled = 0;
        _writer.CaptureDisabled += () => disabled++;

        for (int i = 0; i < 16; i++)
        {
            Assert.Throws<IOException>(() => _writer.CaptureUpTo(StateAt(2), failing, CancellationToken.None));
        }
        Assert.That(disabled, Is.EqualTo(1), "the trip must notify exactly once");

        Assert.DoesNotThrow(() => _writer.CaptureUpTo(StateAt(3), failing, CancellationToken.None));
        Assert.That(disabled, Is.EqualTo(1), "skipped captures after the trip must not re-notify");
    }

    [Test]
    public void Capture_disabled_handler_failure_is_contained()
    {
        SeedGenesisFloor();
        ISnapshotRepository failing = Substitute.For<ISnapshotRepository>();
        failing.TryLeaseInMemoryState(default, default, out _).ThrowsForAnyArgs(new IOException("disk failure"));
        _writer.CaptureDisabled += () => throw new InvalidOperationException("handler failure");

        for (int i = 0; i < 16; i++)
        {
            Assert.Throws<IOException>(() => _writer.CaptureUpTo(StateAt(2), failing, CancellationToken.None),
                "the original capture failure must keep propagating, not the handler's");
        }

        Assert.That(_writer.CaptureHealthy, Is.False);
    }

    [Test]
    public void Permanent_gap_disables_further_capture()
    {
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 1))]);
        _writer.CaptureUpTo(StateAt(1), _repository, CancellationToken.None); // cannot connect: no genesis floor

        CommitBlock(1, 2, accountChanges: [(AddrB, new Account(2, 2))]);
        _writer.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        Span<byte> buffer = stackalloc byte[256];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_writer.LastCapturedBlock, Is.EqualTo(0UL));
            Assert.That(_accountHistory.TryGetAt(2, AccountKey(AddrB), buffer), Is.EqualTo(-1),
                "no rows may be written above a permanent gap");
        }
    }

    [Test]
    public void Capture_binds_block_state_root_for_availability()
    {
        SeedGenesisFloor();
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 1))]);

        _writer.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_reader.IsAvailable(StateAt(1)), Is.True);
            Assert.That(_reader.IsAvailable(new StateId(1, TestItem.KeccakA)), Is.False,
                "a different state root at the same height must not be available (EIP-1898)");
        }
    }

    [Test]
    public void Capture_with_history_disabled_records_nothing()
    {
        FlatDbConfig disabledConfig = new() { HistoryEnabled = false };
        (HistoryAvailability disabledAvailability, HistoryRowFormat disabledRowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, disabledConfig);
        HistoryWriter disabled = new(_db, _historyColumns, disabledConfig, disabledAvailability, disabledRowFormat, LimboLogs.Instance);
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 100))]);

        disabled.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_reader.HasHistoryForBlock(1), Is.False);
            Assert.That(_reader.TryGetAccount(1, AddrA, out _), Is.False);
        }
    }

    [Test]
    public void Windowed_writer_stamps_windowed_format_version_and_it_survives_further_captures()
    {
        (HistoryWriter windowed, _) = CreateWindowedPair(retentionBlocks: 100);
        windowed.SeedGenesis([], StateAt(0).StateRoot);

        Assert.That(HistoryColumnsWriter.GetStampedFormatVersion(_historyColumns), Is.EqualTo((byte?)HistoryAvailability.WindowedFormatVersion),
            "precondition: a windowed writer's seed must stamp the windowed format version");

        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 1))]);
        windowed.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);
        CommitBlock(1, 2, accountChanges: [(AddrA, new Account(2, 2))]);
        windowed.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        Assert.That(HistoryColumnsWriter.GetStampedFormatVersion(_historyColumns), Is.EqualTo((byte?)HistoryAvailability.WindowedFormatVersion),
            "further captures on a windowed writer must never regress the stamp back to the plain format version");
    }

    [Test]
    public void Unwindowed_writer_stamps_the_plain_format_version()
    {
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 1))]);
        _writer.SeedGenesis([], StateAt(0).StateRoot);
        _writer.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        Assert.That(HistoryColumnsWriter.GetStampedFormatVersion(_historyColumns), Is.EqualTo((byte?)HistoryAvailability.FormatVersion),
            "a writer with no window configured must retain today's shipped format version unchanged");
    }

    [Test]
    public void Seeded_genesis_allocations_read_at_every_height()
    {
        _writer.SeedGenesis([new(AddrA, new Account(0, 1000))], StateAt(0).StateRoot);

        bool atGenesis = _reader.TryGetAccount(0, AddrA, out AccountStruct genesisAccount);
        bool atLater = _reader.TryGetAccount(7, AddrA, out AccountStruct laterAccount);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_reader.HasHistoryForBlock(0), Is.True);
            Assert.That(atGenesis, Is.True);
            Assert.That(genesisAccount.Balance, Is.EqualTo((UInt256)1000));
            Assert.That(atLater, Is.True);
            Assert.That(laterAccount.Balance, Is.EqualTo((UInt256)1000));
        }
    }

    [Test]
    public void SeedPivot_OnAWindowedWriter_PublishesWatermarkAndFloor_AndReadsFallThroughToThePersistedFlatColumns()
    {
        (HistoryWriter windowedWriter, HistoryReader windowedReader) = CreateWindowedPair(retentionBlocks: 1000);

        _db.GetColumnDb(FlatDbColumns.Account).PutSpan(FlatAccountKey(AddrA), EncodedAccount(new Account(5, 500)));
        Span<byte> slotValueBuffer = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        int slotValueLength = BaseFlatPersistence.EncodeSlotValue(SlotValue.FromSpanWithoutLeadingZero([0xAA]), RlpWrapSlots, slotValueBuffer);
        _db.GetColumnDb(FlatDbColumns.Storage).PutSpan(StorageKey(AddrA, Slot1), slotValueBuffer[..slotValueLength]);

        StateId pivot = StateAt(100);

        windowedWriter.SeedPivot(100, pivot.StateRoot);

        bool foundAccount = windowedReader.TryGetAccount(100, AddrA, out AccountStruct account);
        bool foundStorage = windowedReader.TryGetStorage(100, AddrA, Slot1, out SlotValue slot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(windowedWriter.LastCapturedBlock, Is.EqualTo(100UL));
            Assert.That(foundAccount, Is.True, "the persisted-flat fallback must resolve the account with no captured row");
            Assert.That(account.Balance, Is.EqualTo((UInt256)500));
            Assert.That(foundStorage, Is.True, "the persisted-flat fallback must resolve the slot with no captured row");
            Assert.That(slot.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0xAA }));
            Assert.That(windowedReader.IsAvailable(pivot), Is.True, "the pivot's own state root must be immediately available");
            Assert.That(windowedReader.IsPrunedBelowFloor(99), Is.True, "the floor publishes at the pivot, so anything below it reports pruned rather than absent");
        }
    }

    [Test]
    public void V3_TwoBlocksTouchingTheSameKeyInOneWalk_ResolveTheOlderTouchesPostValueAsTheNewersPreValue()
    {
        (HistoryWriter windowedWriter, HistoryReader windowedReader) = CreateWindowedPair(retentionBlocks: 1000);

        _db.GetColumnDb(FlatDbColumns.Account).PutSpan(FlatAccountKey(AddrA), EncodedAccount(new Account(0, 100)));
        windowedWriter.SeedGenesis([], StateAt(0).StateRoot);

        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 111))]);
        CommitBlock(1, 2, accountChanges: [(AddrA, new Account(2, 222))]);
        windowedWriter.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        _db.GetColumnDb(FlatDbColumns.Account).PutSpan(FlatAccountKey(AddrA), EncodedAccount(new Account(2, 222)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(windowedReader.TryGetAccount(0, AddrA, out AccountStruct atZero), Is.True);
            Assert.That(atZero.Balance, Is.EqualTo((UInt256)100));
            Assert.That(windowedReader.TryGetAccount(1, AddrA, out AccountStruct atOne), Is.True);
            Assert.That(atOne.Balance, Is.EqualTo((UInt256)111),
                "block 2's pre-value must be block 1's post-value, resolved by the older touch inside the same walk - not the persisted-column fallback, which already moved on");
            Assert.That(windowedReader.TryGetAccount(2, AddrA, out AccountStruct atTwo), Is.True);
            Assert.That(atTwo.Balance, Is.EqualTo((UInt256)222));
        }
    }

    [Test]
    public void V3Read_AtOrBelowWatermark_ResolvesCorrectly_BeforeAndAfterThePersistCatchesUp()
    {
        (HistoryWriter windowedWriter, HistoryReader windowedReader) = CreateWindowedPair(retentionBlocks: 1000);

        _db.GetColumnDb(FlatDbColumns.Account).PutSpan(FlatAccountKey(AddrA), EncodedAccount(new Account(0, 100)));
        windowedWriter.SeedGenesis([], StateAt(0).StateRoot);

        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 111))]);
        windowedWriter.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        bool foundAtZero = windowedReader.TryGetAccount(0, AddrA, out AccountStruct atZero);

        _db.GetColumnDb(FlatDbColumns.Account).PutSpan(FlatAccountKey(AddrA), EncodedAccount(new Account(1, 111)));

        bool foundAtOne = windowedReader.TryGetAccount(1, AddrA, out AccountStruct atOne);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(foundAtZero, Is.True);
            Assert.That(atZero.Balance, Is.EqualTo((UInt256)100), "the captured pre-value row must resolve block 0 correctly, independent of whether the persist has caught up yet");
            Assert.That(foundAtOne, Is.True);
            Assert.That(atOne.Balance, Is.EqualTo((UInt256)111), "the persisted-flat fallback must resolve block 1 (no captured change above it) once the persist reflects this round");
        }
    }

    [Test]
    public void SeedPivot_OnAnUnwindowedWriter_IsANoOp()
    {
        _writer.SeedPivot(100, StateAt(100).StateRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_writer.LastCapturedBlock, Is.EqualTo(0UL));
            Assert.That(_reader.IsPrunedBelowFloor(50), Is.False);
        }
    }

    [Test]
    public void V3_SelfDestruct_MaterializesPerSlotPreValue_ReadableBelowDestructBlock()
    {
        (HistoryWriter windowedWriter, HistoryReader windowedReader) = CreateWindowedPair(retentionBlocks: 1000);

        windowedWriter.SeedGenesis([], StateAt(0).StateRoot);

        CommitBlock(0, 1, storageChanges: [(AddrA, Slot1, HistorySlot(0x0a))]);
        windowedWriter.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        _db.GetColumnDb(FlatDbColumns.Storage).PutSpan(StorageKey(AddrA, Slot1), EncodedHistorySlot(0x0a));

        CommitBlock(1, 2, accountChanges: [(AddrA, null)], selfDestructs: [(AddrA, false)]);
        windowedWriter.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        bool foundBelow = windowedReader.TryGetStorage(1, AddrA, Slot1, out SlotValue belowDestruct);

        _db.GetColumnDb(FlatDbColumns.Storage).Remove(StorageKey(AddrA, Slot1));

        bool foundAt = windowedReader.TryGetStorage(2, AddrA, Slot1, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(foundBelow, Is.True, "the slot's pre-destruct value must be readable strictly below the destruct block");
            Assert.That(belowDestruct.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0x0a }));
            Assert.That(foundAt, Is.False, "the slot must read empty at/after the destruct once the persist has caught up");
        }
    }

    [Test]
    public void V3_AccountCreatedThenDestructed_AllSlotsResolveCorrectlyAcrossTheWindow()
    {
        (HistoryWriter windowedWriter, HistoryReader windowedReader) = CreateWindowedPair(retentionBlocks: 1000);

        windowedWriter.SeedGenesis([], StateAt(0).StateRoot);

        CommitBlock(0, 1,
            accountChanges: [(AddrA, new Account(1, 100))],
            storageChanges: [(AddrA, Slot1, HistorySlot(0x0a)), (AddrA, Slot2, HistorySlot(0x0b))]);
        windowedWriter.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        _db.GetColumnDb(FlatDbColumns.Account).PutSpan(FlatAccountKey(AddrA), EncodedAccount(new Account(1, 100)));
        _db.GetColumnDb(FlatDbColumns.Storage).PutSpan(StorageKey(AddrA, Slot1), EncodedHistorySlot(0x0a));
        _db.GetColumnDb(FlatDbColumns.Storage).PutSpan(StorageKey(AddrA, Slot2), EncodedHistorySlot(0x0b));

        CommitBlock(1, 2, accountChanges: [(AddrA, null)], selfDestructs: [(AddrA, false)]);
        windowedWriter.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        bool foundAccountBelow = windowedReader.TryGetAccount(1, AddrA, out AccountStruct accountBelow);
        bool foundSlot1Below = windowedReader.TryGetStorage(1, AddrA, Slot1, out SlotValue slot1Below);
        bool foundSlot2Below = windowedReader.TryGetStorage(1, AddrA, Slot2, out SlotValue slot2Below);

        _db.GetColumnDb(FlatDbColumns.Account).Remove(FlatAccountKey(AddrA));
        _db.GetColumnDb(FlatDbColumns.Storage).Remove(StorageKey(AddrA, Slot1));
        _db.GetColumnDb(FlatDbColumns.Storage).Remove(StorageKey(AddrA, Slot2));

        bool foundAccountAt = windowedReader.TryGetAccount(2, AddrA, out _);
        bool foundSlot1At = windowedReader.TryGetStorage(2, AddrA, Slot1, out _);
        bool foundSlot2At = windowedReader.TryGetStorage(2, AddrA, Slot2, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(foundAccountBelow, Is.True);
            Assert.That(accountBelow.Balance, Is.EqualTo((UInt256)100));
            Assert.That(foundSlot1Below, Is.True);
            Assert.That(slot1Below.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0x0a }));
            Assert.That(foundSlot2Below, Is.True);
            Assert.That(slot2Below.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0x0b }));

            Assert.That(foundAccountAt, Is.False, "the account must be a tombstone at/after its own destruct block");
            Assert.That(foundSlot1At, Is.False, "every persisted slot must be dead at/after the destruct, not just the one explicitly touched");
            Assert.That(foundSlot2At, Is.False);
        }
    }

    [Test]
    public void V3_SlotDestructedAndRewrittenInSameBlock_ReadsPreValueBelowAndResurrectedValueAt()
    {
        (HistoryWriter windowedWriter, HistoryReader windowedReader) = CreateWindowedPair(retentionBlocks: 1000);

        windowedWriter.SeedGenesis([], StateAt(0).StateRoot);

        CommitBlock(0, 1, storageChanges: [(AddrA, Slot1, HistorySlot(0x0a))]);
        windowedWriter.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);
        _db.GetColumnDb(FlatDbColumns.Storage).PutSpan(StorageKey(AddrA, Slot1), EncodedHistorySlot(0x0a));

        CommitBlock(1, 2, storageChanges: [(AddrA, Slot1, HistorySlot(0x0b))], selfDestructs: [(AddrA, false)]);
        windowedWriter.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        bool foundBelow = windowedReader.TryGetStorage(1, AddrA, Slot1, out SlotValue belowDestruct);

        _db.GetColumnDb(FlatDbColumns.Storage).PutSpan(StorageKey(AddrA, Slot1), EncodedHistorySlot(0x0b));

        bool foundAt = windowedReader.TryGetStorage(2, AddrA, Slot1, out SlotValue atResurrection);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(foundBelow, Is.True);
            Assert.That(belowDestruct.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0x0a }),
                "the pre-destruct value must still be readable strictly below the combined destruct+rewrite block");
            Assert.That(foundAt, Is.True);
            Assert.That(atResurrection.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0x0b }),
                "as of the combined block, the resurrected value must win, not the destruct's wipe");
        }
    }

    [Test]
    public void V3_DestructAndRewriteInSameBlock_WithHigherTouchInSameWalk()
    {
        (HistoryWriter windowedWriter, HistoryReader windowedReader) = CreateWindowedPair(retentionBlocks: 1000);

        _db.GetColumnDb(FlatDbColumns.Storage).PutSpan(StorageKey(AddrA, Slot1), EncodedHistorySlot(0x0a));
        windowedWriter.SeedGenesis([], StateAt(0).StateRoot);

        CommitBlock(0, 1, storageChanges: [(AddrA, Slot1, HistorySlot(0x0b))], selfDestructs: [(AddrA, false)]);
        CommitBlock(1, 2, storageChanges: [(AddrA, Slot1, HistorySlot(0x0c))]);
        windowedWriter.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        _db.GetColumnDb(FlatDbColumns.Storage).PutSpan(StorageKey(AddrA, Slot1), EncodedHistorySlot(0x0c));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(windowedReader.TryGetStorage(0, AddrA, Slot1, out SlotValue at0), Is.True);
            Assert.That(at0.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0x0a }));

            Assert.That(windowedReader.TryGetStorage(1, AddrA, Slot1, out SlotValue at1), Is.True,
                "the resurrected value must be readable as of the combined destruct+rewrite block");
            Assert.That(at1.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0x0b }));

            Assert.That(windowedReader.TryGetStorage(2, AddrA, Slot1, out SlotValue at2), Is.True);
            Assert.That(at2.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0x0c }));
        }
    }

    [Test]
    public void V3_SlotFirstWrittenBelowADestructInTheSameWalk_ReadsItsValueBetweenTheTwoBlocks()
    {
        (HistoryWriter windowedWriter, HistoryReader windowedReader) = CreateWindowedPair(retentionBlocks: 1000);

        _db.GetColumnDb(FlatDbColumns.Storage).PutSpan(StorageKey(AddrA, Slot2), EncodedHistorySlot(0x0b));
        windowedWriter.SeedGenesis([], StateAt(0).StateRoot);

        CommitBlock(0, 1, storageChanges: [(AddrA, Slot1, HistorySlot(0x0a))]);
        CommitBlock(1, 2, accountChanges: [(AddrA, null)], selfDestructs: [(AddrA, false)]);
        windowedWriter.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        _db.GetColumnDb(FlatDbColumns.Storage).Remove(StorageKey(AddrA, Slot1));
        _db.GetColumnDb(FlatDbColumns.Storage).Remove(StorageKey(AddrA, Slot2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(windowedReader.TryGetStorage(0, AddrA, Slot1, out _), Is.False,
                "the slot did not exist before the block that first wrote it");
            Assert.That(windowedReader.TryGetStorage(1, AddrA, Slot1, out SlotValue between), Is.True,
                "the destruct one block up never enumerated this slot - it was not persisted yet - so its value has to be spliced in from the walk itself");
            Assert.That(between.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0x0a }));
            Assert.That(windowedReader.TryGetStorage(2, AddrA, Slot1, out _), Is.False,
                "the destruct block itself must read empty");
            Assert.That(windowedReader.TryGetStorage(1, AddrA, Slot2, out SlotValue persisted), Is.True);
            Assert.That(persisted.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0x0b }),
                "the persisted slot's own pre-destruct value must survive the splice unchanged");
        }
    }

    [Test]
    public void V3_SelfDestruct_AboveEnumerationCap_PoisonsAccount_ReadFailsClosed()
    {
        (HistoryWriter windowedWriter, HistoryReader windowedReader) = CreateWindowedPair(retentionBlocks: 1000);

        windowedWriter.SeedGenesis([], StateAt(0).StateRoot);

        for (UInt256 slot = 1; slot <= HistoryWriter.DestructSlotEnumerationCap + 1; slot++)
        {
            _db.GetColumnDb(FlatDbColumns.Storage).PutSpan(StorageKey(AddrA, slot), EncodedHistorySlot(0x01));
        }

        CommitBlock(0, 1, accountChanges: [(AddrA, null)], selfDestructs: [(AddrA, false)]);
        windowedWriter.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        Assert.That(() => windowedReader.TryGetStorage(0, AddrA, 999999, out _),
            Throws.InstanceOf<InvalidOperationException>(),
            "a slot the over-cap destruct wrote no row for must fail closed rather than silently report absent");
    }

    [Test]
    public void V3_SelfDestruct_AboveEnumerationCap_ARecordedRowStillAnswers_OnlyTheLiveFallbackFailsClosed()
    {
        (HistoryWriter windowedWriter, HistoryReader windowedReader) = CreateWindowedPair(retentionBlocks: 1000);

        windowedWriter.SeedGenesis([], StateAt(0).StateRoot);

        CommitBlock(0, 1, storageChanges: [(AddrA, Slot1, HistorySlot(0x0a))]);
        CommitBlock(1, 2, storageChanges: [(AddrA, Slot1, HistorySlot(0x0b))]);
        windowedWriter.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        for (UInt256 slot = 100; slot <= 100 + HistoryWriter.DestructSlotEnumerationCap + 1; slot++)
        {
            _db.GetColumnDb(FlatDbColumns.Storage).PutSpan(StorageKey(AddrA, slot), EncodedHistorySlot(0x01));
        }

        CommitBlock(2, 3, accountChanges: [(AddrA, null)], selfDestructs: [(AddrA, false)]);
        windowedWriter.CaptureUpTo(StateAt(3), _repository, CancellationToken.None);

        bool found = windowedReader.TryGetStorage(1, AddrA, Slot1, out SlotValue belowDestruct);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True, "a recorded pre-value row is authoritative below the destruct - the poison only covers reads that would fall through to live state");
            Assert.That(belowDestruct.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0x0a }));
            Assert.That(() => windowedReader.TryGetStorage(1, AddrA, 999999, out _),
                Throws.InstanceOf<InvalidOperationException>(),
                "a slot with no recorded row below an over-cap destruct must still fail closed - absent is indistinguishable from missed by the cap");
        }
    }

    [Test]
    public void V3_OverCapDestruct_ARowWrittenByALaterWalk_DoesNotAnswerReadsBelowTheDestruct()
    {
        (HistoryWriter windowedWriter, HistoryReader windowedReader) = CreateWindowedPair(retentionBlocks: 1000);

        windowedWriter.SeedGenesis([], StateAt(0).StateRoot);

        for (UInt256 slot = 1; slot <= HistoryWriter.DestructSlotEnumerationCap + 1; slot++)
        {
            _db.GetColumnDb(FlatDbColumns.Storage).PutSpan(StorageKey(AddrA, slot), EncodedHistorySlot(0x01));
        }

        CommitBlock(0, 1, accountChanges: [(AddrA, null)], selfDestructs: [(AddrA, false)]);
        windowedWriter.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        UInt256 missedSlot = 0;
        for (UInt256 slot = 1; slot <= HistoryWriter.DestructSlotEnumerationCap + 1; slot++)
        {
            try
            {
                windowedReader.TryGetStorage(0, AddrA, slot, out _);
            }
            catch (InvalidOperationException)
            {
                missedSlot = slot;
                break;
            }
        }

        Assert.That(missedSlot, Is.Not.EqualTo((UInt256)0), "the capped enumeration must have missed a slot for the scenario to exist");

        for (UInt256 slot = 1; slot <= HistoryWriter.DestructSlotEnumerationCap + 1; slot++)
        {
            _db.GetColumnDb(FlatDbColumns.Storage).Remove(StorageKey(AddrA, slot));
        }

        CommitBlock(1, 2, storageChanges: [(AddrA, missedSlot, HistorySlot(0x0b))]);
        windowedWriter.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        Assert.That(() => windowedReader.TryGetStorage(0, AddrA, missedSlot, out _),
            Throws.InstanceOf<InvalidOperationException>(),
            "the rewrite's row resolved its pre-value through a live column the destruct already truncated - believing it reports the slot unset where it held a value");
    }

    [Test]
    public void V3_OverCapDestruct_ASlotRewrittenAboveItInTheSameWalk_ReadsUnsetBetweenThem()
    {
        (HistoryWriter windowedWriter, HistoryReader windowedReader) = CreateWindowedPair(retentionBlocks: 1000);

        windowedWriter.SeedGenesis([], StateAt(0).StateRoot);

        int slotCount = HistoryWriter.DestructSlotEnumerationCap + 1;
        (Address Address, UInt256 Slot, SlotValue? Value)[] rewrites = new (Address, UInt256, SlotValue?)[slotCount];
        for (int i = 0; i < slotCount; i++)
        {
            UInt256 slot = (UInt256)(i + 1);
            _db.GetColumnDb(FlatDbColumns.Storage).PutSpan(StorageKey(AddrA, slot), EncodedHistorySlot(0x01));
            rewrites[i] = (AddrA, slot, HistorySlot(0x0b));
        }

        CommitBlock(0, 1, accountChanges: [(AddrA, null)], selfDestructs: [(AddrA, false)]);
        CommitBlock(1, 2, storageChanges: rewrites);
        windowedWriter.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < slotCount; i++)
            {
                UInt256 slot = (UInt256)(i + 1);
                Assert.That(windowedReader.TryGetStorage(1, AddrA, slot, out _), Is.False,
                    $"slot {slot} was destroyed at block 1 and rewritten at block 2 in the same walk - between them it is unset, not the resurrected pre-destruct value");
                Assert.That(windowedReader.TryGetStorage(0, AddrA, slot, out SlotValue beforeDestruct), Is.True,
                    $"slot {slot} held its persisted value below the destruct");
                Assert.That(beforeDestruct.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0x01 }));
            }
        }
    }

    [Test]
    public void SeedPivot_InsideTheAlreadyCapturedWindow_Throws_AndWritesNothing()
    {
        (HistoryWriter windowedWriter, HistoryReader windowedReader) = CreateWindowedPair(retentionBlocks: 1000);

        windowedWriter.SeedGenesis([], StateAt(0).StateRoot);
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 111))]);
        CommitBlock(1, 2, accountChanges: [(AddrA, new Account(2, 222))]);
        windowedWriter.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        Assert.That(() => windowedWriter.SeedPivot(1, TestItem.KeccakA),
            Throws.InvalidOperationException.With.Message.Contains("watermark"),
            "a pivot inside the captured window replaces the live state its history resolves through - it must refuse, not re-seed");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(windowedWriter.LastCapturedBlock, Is.EqualTo(2UL), "the watermark must be untouched by the refused seed");
            Assert.That(windowedReader.IsAvailable(StateAt(1)), Is.True, "block 1's captured marker must not have been overwritten before the refusal");
            Assert.That(windowedReader.IsPrunedBelowFloor(0), Is.False, "no floor may publish from a refused seed");
        }
    }

    [Test]
    public void Capture_over_converted_range_reads_persisted_bases()
    {
        SeedGenesisFloor();
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 1)), (AddrB, new Account(1, 100))]);
        CommitBlock(1, 2, accountChanges: [(AddrA, new Account(2, 2)), (AddrB, null)], storageChanges: [(AddrA, Slot1, HistorySlot(0x0a))]);
        CommitBlock(2, 3, accountChanges: [(AddrA, new Account(3, 3))]);
        CommitBlock(3, 4, accountChanges: [(AddrA, new Account(4, 4))]);
        ConvertToPersistedTier(2);
        ConvertToPersistedTier(3);

        _writer.CaptureUpTo(StateAt(4), _repository, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            for (ulong block = 1; block <= 4; block++)
            {
                Assert.That(_reader.HasHistoryForBlock(block), Is.True, $"block {block} must be available");
                Assert.That(_reader.TryGetAccount(block, AddrA, out AccountStruct account), Is.True, $"account must resolve at block {block}");
                Assert.That(account.Balance, Is.EqualTo((UInt256)block), $"balance at block {block} must be that block's own value");
            }

            AssertStorageAt(2, Slot1, "0a");
            Assert.That(_reader.TryGetAccount(1, AddrB, out _), Is.True, "AddrB must exist before its deletion");
            Assert.That(_reader.TryGetAccount(2, AddrB, out _), Is.False, "AddrB's deletion must round-trip through the persisted base");
        }
    }

    [Test]
    public void Flag_on_keeps_tip_reads_correct_and_populates_history()
    {
        SeedGenesisFloor();
        const int blockCount = 6;
        for (ulong block = 1; block <= blockCount; block++)
        {
            CommitBlock(
                block - 1, block,
                accountChanges: [(AddrA, new Account(block, (UInt256)(block * 10)))],
                storageChanges: [(AddrA, Slot1, RegressionSlotFor(block))]);
        }

        Assert.DoesNotThrow(() => _writer.CaptureUpTo(StateAt(blockCount), _repository, CancellationToken.None));

        Account? tipAccount;
        byte[]? tipSlot;
        using (ReadOnlySnapshotBundle tip = TipBundle(blockCount, blockCount))
        {
            tipAccount = tip.GetAccount(AddrA);
            tipSlot = tip.GetSlot(AddrA, Slot1, tip.DetermineSelfDestructSnapshotIdx(AddrA));
        }

        bool historyHasMidpoint = _reader.TryGetAccount(blockCount / 2, AddrA, out AccountStruct midpoint);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tipAccount, Is.Not.Null);
            Assert.That(tipAccount!.Nonce, Is.EqualTo((ulong)blockCount));
            Assert.That(tipAccount.Balance, Is.EqualTo((UInt256)(blockCount * 10)));
            Assert.That(tipSlot, Is.EqualTo(RegressionSlotBytes(blockCount)));

            Assert.That(_writer.LastCapturedBlock, Is.EqualTo(blockCount));
            Assert.That(historyHasMidpoint, Is.True);
            Assert.That(midpoint.Nonce, Is.EqualTo((ulong)(blockCount / 2)));
        }
    }

    [Test]
    public void Capture_after_real_compaction_has_no_gaps()
    {
        SeedGenesisFloor();
        const int compactSize = 8;
        const int blockCount = 24; // 3 full compaction windows at CompactSize 8.

        FlatDbConfig compactionConfig = new() { CompactSize = compactSize, CompactionOffset = 0 };
        CompactionSchedule schedule = new(new MemDb(), compactionConfig, LimboLogs.Instance);
        SnapshotCompactor compactor = new(compactionConfig, schedule, _resourcePool, _repository, LimboLogs.Instance);

        for (ulong block = 1; block <= blockCount; block++)
        {
            CommitBlock(
                block - 1, block,
                accountChanges: [(AddrA, new Account(block, (UInt256)block))],
                storageChanges: [(AddrA, Slot1, CompactionSlotFor(block))]);

            compactor.DoCompactSnapshot(StateAt(block));
        }

        Assert.That(_repository.CompactedSnapshotCount, Is.GreaterThan(0), "Expected the real compactor to have coalesced at least one window.");

        _writer.CaptureUpTo(StateAt(blockCount), _repository, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            for (ulong block = 1; block <= blockCount; block++)
            {
                bool foundAccount = _reader.TryGetAccount(block, AddrA, out AccountStruct account);
                Assert.That(foundAccount, Is.True, $"Account missing at block {block} (capture gap).");
                Assert.That(account.Nonce, Is.EqualTo((ulong)block), $"Account at block {block} resolved to the wrong (earlier-boundary) value.");
                Assert.That(account.Balance, Is.EqualTo((UInt256)block), $"Account balance at block {block} resolved to the wrong value.");

                bool foundSlot = _reader.TryGetStorage(block, AddrA, Slot1, out SlotValue slot);
                Assert.That(foundSlot, Is.True, $"Storage missing at block {block} (capture gap).");
                Assert.That(slot.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(CompactionSlotBytes(block)), $"Storage at block {block} resolved to the wrong value.");
            }
        }
    }

    private ReadOnlySnapshotBundle TipBundle(ulong tip, int estimatedSize)
    {
        AssembledSnapshotResult assembled = _repository.AssembleSnapshots(StateAt(tip), StateAt(0), estimatedSize);
        assembled.Persisted.Dispose(); // in-memory tip bundle: no persisted tier
        return new ReadOnlySnapshotBundle(assembled.InMemory, new NoopPersistenceReader(), recordDetailedMetrics: false, PersistedSnapshotStack.Empty());
    }

    private void AssertStorageAt(ulong readBlock, UInt256 slot, string? expectedHex)
    {
        bool found = _reader.TryGetStorage(readBlock, AddrA, slot, out SlotValue value);

        if (expectedHex is null)
        {
            Assert.That(found, Is.False, $"slot {slot} must read empty at block {readBlock}");
        }
        else
        {
            Assert.That(found, Is.True, $"slot {slot} must be present at block {readBlock}");
            Assert.That(value.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(Convert.FromHexString(expectedHex)),
                $"slot {slot} resolved to the wrong value at block {readBlock}");
        }
    }

    private static byte[] RegressionSlotBytes(ulong block) => [0xAB, (byte)block];

    private static SlotValue RegressionSlotFor(ulong block) => SlotValue.FromSpanWithoutLeadingZero(RegressionSlotBytes(block));

    private static byte[] CompactionSlotBytes(ulong block) => [0xAB, (byte)(block >> 8), (byte)block];

    private static SlotValue CompactionSlotFor(ulong block) => SlotValue.FromSpanWithoutLeadingZero(CompactionSlotBytes(block));

    [Test]
    public void Accounts_read_back_at_a_height_reproduce_that_height_state_root()
    {
        SeedGenesisFloor();
        Account accountA = new(3, 300);
        Account accountB = new(7, 700);

        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 100)), (AddrB, new Account(1, 100))]);
        CommitBlock(1, 2, accountChanges: [(AddrA, accountA), (AddrB, accountB)]);
        _writer.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        StateTree expected = new(new RawScopedTrieStore(new MemDb()), LimboLogs.Instance);
        expected.Set(AddrA, accountA);
        expected.Set(AddrB, accountB);
        expected.UpdateRootHash();

        foreach (KeyValuePair<byte[], byte[]> row in _historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory).GetAll())
        {
            Assert.That(row.Key.Length - sizeof(ulong), Is.EqualTo(Hash256.Size),
                "an account row has to carry the whole trie path, otherwise a scan of the column cannot place its leaf and no root can be rebuilt from history");
        }

        StateTree rebuilt = new(new RawScopedTrieStore(new MemDb()), LimboLogs.Instance);
        foreach (Address address in new[] { AddrA, AddrB })
        {
            Assert.That(_reader.TryGetAccount(2, address, out AccountStruct account), Is.True,
                $"account {address} must be readable from history at the height whose root is being rebuilt");
            rebuilt.Set(address, new Account(account.Nonce, account.Balance, account.StorageRoot.ToCommitment(), account.CodeHash.ToCommitment()));
        }

        rebuilt.UpdateRootHash();

        Assert.That(rebuilt.RootHash, Is.EqualTo(expected.RootHash),
            "a state root rebuilt from what history returns at a height must equal the root of a trie built directly from the same accounts - if it does not, history cannot be checked against headers whatever shape the key takes");
    }

    private void CommitBlock(
        ulong fromBlock,
        ulong toBlock,
        (Address Address, Account? Account)[]? accountChanges = null,
        (Address Address, UInt256 Slot, SlotValue? Value)[]? storageChanges = null,
        (Address Address, bool IsNewAccount)[]? selfDestructs = null)
    {
        Snapshot snapshot = _resourcePool.CreateSnapshot(StateAt(fromBlock), StateAt(toBlock), ResourcePool.Usage.ReadOnlyProcessingEnv);

        if (accountChanges is not null)
            foreach ((Address address, Account? account) in accountChanges)
                snapshot.Content.Accounts[address] = account;

        if (storageChanges is not null)
            foreach ((Address address, UInt256 slot, SlotValue? value) in storageChanges)
                snapshot.Content.Storages[(address, slot)] = value;

        if (selfDestructs is not null)
            foreach ((Address address, bool isNewAccount) in selfDestructs)
                snapshot.Content.SelfDestructedStorageAddresses[address] = isNewAccount;

        Assert.That(_repository.TryAdd(snapshot, SnapshotTier.InMemoryBase), Is.True);
        _repository.AddStateId(StateAt(toBlock));
    }

    private void CommitGenesis(
        (Address Address, Account? Account)[]? accountChanges = null,
        (Address Address, UInt256 Slot, SlotValue? Value)[]? storageChanges = null)
    {
        Snapshot snapshot = _resourcePool.CreateSnapshot(StateId.PreGenesis, StateAt(0), ResourcePool.Usage.ReadOnlyProcessingEnv);

        if (accountChanges is not null)
            foreach ((Address address, Account? account) in accountChanges)
                snapshot.Content.Accounts[address] = account;

        if (storageChanges is not null)
            foreach ((Address address, UInt256 slot, SlotValue? value) in storageChanges)
                snapshot.Content.Storages[(address, slot)] = value;

        Assert.That(_repository.TryAdd(snapshot, SnapshotTier.InMemoryBase), Is.True);
        _repository.AddStateId(StateAt(0));
    }

    private void ConvertToPersistedTier(ulong block)
    {
        Assert.That(_repository.TryLeaseInMemoryState(StateAt(block), SnapshotTier.InMemoryBase, out Snapshot? snapshot), Is.True,
            $"precondition: block {block} base must be in memory to convert");
        using (snapshot)
        {
            _tier.Loader.ConvertAndRegister(snapshot!);
        }

        _repository.RemoveAndReleaseInMemoryKnownState(StateAt(block), SnapshotTier.InMemoryBase);
    }

    private void SeedGenesisFloor() => _writer.SeedGenesis([], StateAt(0).StateRoot);

    private (HistoryWriter Writer, HistoryReader Reader) CreateWindowedPair(ulong retentionBlocks)
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetentionBlocks = retentionBlocks };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        HistoryWriter writer = new(_db, _historyColumns, config, availability, rowFormat, LimboLogs.Instance);
        HistoryReader reader = new(_db, _historyColumns, availability, rowFormat, LimboLogs.Instance);
        return (writer, reader);
    }

    private static StateId StateAt(ulong blockNumber)
    {
        Span<byte> root = stackalloc byte[32];
        root[0] = (byte)blockNumber;
        return new StateId(blockNumber, new ValueHash256(root));
    }

    private static byte[] AccountKey(Address address)
    {
        Span<byte> buffer = stackalloc byte[HistoryKeyLayout.AccountKeyLength];
        return address.ToAccountPath.Bytes.ToArray();
    }

    private static byte[] FlatAccountKey(Address address)
    {
        Span<byte> buffer = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
        return BaseFlatPersistence.EncodeAccountKeyHashed(buffer, address.ToAccountPath).ToArray();
    }

    private static byte[] StorageKey(Address address, UInt256 slot)
    {
        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
        Span<byte> buffer = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        return BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(buffer, address.ToAccountPath, slotHash).ToArray();
    }

    private static byte[] EncodedAccount(Account account)
    {
        using ArrayPoolSpan<byte> rlp = AccountDecoder.Slim.EncodeToArrayPoolSpan(account);
        return ((ReadOnlySpan<byte>)rlp).ToArray();
    }

    private static byte[] EncodedSlot(ReadOnlySpan<byte> rawSlotBytes)
    {
        Span<byte> buffer = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        int written = BaseFlatPersistence.EncodeSlotValue(new SlotValue(rawSlotBytes), RlpWrapSlots, buffer);
        return buffer[..written].ToArray();
    }

    private static SlotValue Slot(params byte[] bytes) => new(bytes);

    private static SlotValue HistorySlot(params byte[] bytes) => SlotValue.FromSpanWithoutLeadingZero(bytes);

    private static byte[] EncodedHistorySlot(params byte[] bytes)
    {
        Span<byte> buffer = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        int written = BaseFlatPersistence.EncodeSlotValue(HistorySlot(bytes), RlpWrapSlots, buffer);
        return buffer[..written].ToArray();
    }

    public enum ExpectedKind
    {
        Absent,
        Tombstone,
        Value
    }
}
