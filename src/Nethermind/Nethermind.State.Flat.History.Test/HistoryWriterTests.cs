// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
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
        _reader = new HistoryReader(_db, _historyColumns, config, _availability, _rowFormat, LimboLogs.Instance);
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

    // AddrA: (nonce 1, balance 100) @ b1, overwritten to (nonce 2, balance 200) @ b2, deleted @ b3.
    // Nonce == block number for the committed values, so the expected account is reconstructible from readBlock.
    [TestCase(0ul, 0ul, ExpectedKind.Absent)]   // before the first change -> absent at that height
    [TestCase(1ul, 100ul, ExpectedKind.Value)]
    [TestCase(2ul, 200ul, ExpectedKind.Value)]
    [TestCase(3ul, 0ul, ExpectedKind.Tombstone)] // deleted
    [TestCase(4ul, 0ul, ExpectedKind.Tombstone)]
    public void Captures_account_value_as_of_block(ulong readBlock, ulong balance, ExpectedKind kind)
    {
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

    // AddrA/Slot1: 0x0a @ b1, overwritten to 0x0bbb @ b2, zeroed (removed) @ b3.
    [TestCase(0ul, null)]
    [TestCase(1ul, "0a")]
    [TestCase(2ul, "0bbb")]
    [TestCase(3ul, "", true)]
    [TestCase(4ul, "", true)]
    public void Captures_storage_value_as_of_block(ulong readBlock, string? expectedHex, bool expectTombstone = false)
    {
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

    // (a) created @1, (a) self-destructed @2 (null tombstone), (c) re-created @3 with a new value.
    [TestCase(0ul, 0ul)]   // before any change -> absent
    [TestCase(1ul, 100ul)] // created
    [TestCase(2ul, 0ul)]   // self-destructed -> absent
    [TestCase(3ul, 300ul)] // re-created
    [TestCase(4ul, 300ul)] // still present afterwards
    public void Account_selfdestruct_then_recreate_reads_per_height(ulong readBlock, ulong expectedBalance)
    {
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

    // Slot written @1, killed @2 by a per-slot clear (tombstone) or a self-destruct (range-delete in the live
    // column, so only the clear marker can kill the @1 value), re-written @3. The kill block optionally lives in
    // the persisted tier (converted by long-finality Phase 2), so the walk crosses tiers mid-range.
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

    // A destruct and a re-creation in the same block: the snapshot's slot values are the post-destruct state,
    // so they win over the same-block clear (mirrors the live column's destruct-then-write batch order).
    [Test]
    public void Storage_destructed_and_rewritten_in_same_block_reads_the_rewrite()
    {
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

    // IsNewAccount == true means the account had no persisted storage before the destruct; the live column skips
    // the range-delete in that case, so no clear is recorded and pre-existing history stays visible.
    [Test]
    public void Selfdestruct_of_account_without_persisted_storage_records_no_clear()
    {
        CommitBlock(0, 1, storageChanges: [(AddrA, Slot1, HistorySlot(0x0a))]);
        CommitBlock(1, 2, selfDestructs: [(AddrA, true)]);

        _writer.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        AssertStorageAt(3, Slot1, "0a");
    }

    // an EIP-158-style empty account (nonce 0, balance 0) must round-trip as a
    // present account, not as a deletion tombstone.
    [Test]
    public void Empty_account_round_trips_as_present_not_tombstone()
    {
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

    // Genesis allocations never touched again must be captured on the first walk and resolve at every later height.
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

    // A capture that cannot walk down to the genesis floor (no genesis snapshot, no seeded floor) never connects, so
    // the watermark must stay unset — reads report no history rather than a pre-gap value.
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

    // Archive-clone mode: an unconnected walk records a pending range instead of disabling capture, so the blocks
    // processed while the clone backfills are never lost; reads stay refused until the ranges connect.
    [Test]
    public void Detached_capture_records_a_pending_range_instead_of_disabling()
    {
        HistoryWriter writer = CreateCloneModeWriter();
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 1))]);
        CommitBlock(1, 2, accountChanges: [(AddrA, new Account(2, 2))]);

        writer.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(writer.CaptureHealthy, Is.True, "detached capture must keep running, not disable itself");
            Assert.That(writer.LastCapturedBlock, Is.EqualTo(0UL), "nothing is served before the ranges connect");
            Assert.That(_availability.TryGetPendingCaptureRange(out ulong first, out ulong last), Is.True);
            Assert.That((first, last), Is.EqualTo((1UL, 2UL)));
            Assert.That(_reader.HasHistoryForBlock(2), Is.False);
        }
    }

    [Test]
    public void Detached_capture_extends_the_pending_range_across_persists()
    {
        HistoryWriter writer = CreateCloneModeWriter();
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 1))]);
        CommitBlock(1, 2, accountChanges: [(AddrA, new Account(2, 2))]);
        writer.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        CommitBlock(2, 3, accountChanges: [(AddrA, new Account(3, 3))]);
        writer.CaptureUpTo(StateAt(3), _repository, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_availability.TryGetPendingCaptureRange(out ulong first, out ulong last), Is.True);
            Assert.That((first, last), Is.EqualTo((1UL, 3UL)));
            Assert.That(writer.LastCapturedBlock, Is.EqualTo(0UL));
        }
    }

    [Test]
    public void Pending_range_merges_once_the_imported_watermark_reaches_it()
    {
        HistoryWriter writer = CreateCloneModeWriter();
        ulong advancedTo = 0;
        writer.WatermarkAdvanced += w => advancedTo = w;
        Account atBlock1 = new(1, 11);
        Account atBlock2 = new(2, 22);
        CommitBlock(0, 1, accountChanges: [(AddrA, atBlock1)]);
        CommitBlock(1, 2, accountChanges: [(AddrA, atBlock2)]);
        writer.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        _availability.PublishWatermark(0, _rowFormat.FormatVersion);

        CommitBlock(2, 3, accountChanges: [(AddrA, new Account(3, 33))]);
        writer.CaptureUpTo(StateAt(3), _repository, CancellationToken.None);

        Span<byte> buffer = stackalloc byte[256];
        ReadOnlySpan<byte> flatKey = AccountKey(AddrA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(writer.LastCapturedBlock, Is.EqualTo(3UL), "the union is contiguous from genesis and fully served");
            Assert.That(_availability.TryGetPendingCaptureRange(out _, out _), Is.False, "the pending range is consumed by the merge");
            Assert.That(advancedTo, Is.EqualTo(3UL));
            Assert.That(_reader.HasHistoryForBlock(2), Is.True);
            Assert.That(buffer[.._accountHistory.TryGetAt(1, flatKey, buffer)].ToArray(), Is.EqualTo(EncodedAccount(atBlock1)));
            Assert.That(buffer[.._accountHistory.TryGetAt(2, flatKey, buffer)].ToArray(), Is.EqualTo(EncodedAccount(atBlock2)));
        }
    }

    // An imported watermark that stops short of the pending range's bottom leaves a hole no read may cross:
    // nothing merges, nothing above the watermark is served, and the pending capture keeps recording.
    [Test]
    public void Pending_range_with_a_hole_below_it_stays_unpublished()
    {
        HistoryWriter writer = CreateCloneModeWriter();
        CommitBlock(2, 3, accountChanges: [(AddrA, new Account(3, 3))]);
        CommitBlock(3, 4, accountChanges: [(AddrA, new Account(4, 4))]);
        writer.CaptureUpTo(StateAt(4), _repository, CancellationToken.None);

        _availability.PublishWatermark(1, _rowFormat.FormatVersion);

        CommitBlock(4, 5, accountChanges: [(AddrA, new Account(5, 5))]);
        writer.CaptureUpTo(StateAt(5), _repository, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(writer.LastCapturedBlock, Is.EqualTo(1UL), "the watermark must not advance over the hole at block 2");
            Assert.That(_availability.TryGetPendingCaptureRange(out ulong first, out ulong last), Is.True);
            Assert.That((first, last), Is.EqualTo((3UL, 5UL)), "the pending capture keeps recording above the hole");
            Assert.That(_reader.HasHistoryForBlock(3), Is.False);
        }
    }

    private HistoryWriter CreateCloneModeWriter() =>
        new(_db, _historyColumns, new FlatDbConfig { HistoryEnabled = true, HistoryArchiveCloneEnabled = true }, _availability, _rowFormat, LimboLogs.Instance);

    [Test]
    public void Recapture_after_restart_is_idempotent_and_extends_from_watermark()
    {
        SeedGenesisFloor();
        Account atBlock1 = new(1, 11);
        Account atBlock2 = new(2, 22);
        CommitBlock(0, 1, accountChanges: [(AddrA, atBlock1)]);
        CommitBlock(1, 2, accountChanges: [(AddrA, atBlock2)]);
        _writer.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        // "Restart": a fresh writer over the same columns, replay re-captures the same head, then extends.
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

    // Capture batches commit markers before any watermark publish; the format stamp must ride the same batch or a
    // restart in between reads the index as pre-release v1 and refuses startup.
    [Test]
    public void Capture_without_publish_still_stamps_format()
    {
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 1))]);
        _writer.CaptureUpTo(StateAt(1), _repository, CancellationToken.None); // unconnected: markers written, watermark never published

        Assert.DoesNotThrow(() => _ = new HistoryReader(_db, _historyColumns, new FlatDbConfig { HistoryEnabled = true }, _availability, _rowFormat, LimboLogs.Instance));
    }

    // Only a completed capture may report health: config cannot prove the hook is wired, and the seed proves
    // only the floor.
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

    // A number-only connect would advance the watermark over a reorged pre-finalization capture.
    [Test]
    public void Reorged_capture_at_the_connect_point_refuses_to_advance_the_watermark()
    {
        SeedGenesisFloor();
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 11))]);
        _writer.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);
        Assert.That(_writer.LastCapturedBlock, Is.EqualTo(1UL));

        // The reorged branch: same height 1, different state root, continuing to block 2.
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

    // After the breaker trips, capture degrades to "no more history" and persistence resumes.
    [Test]
    public void Repeated_capture_failures_trip_the_breaker_and_let_persistence_resume()
    {
        SeedGenesisFloor();
        ISnapshotRepository failing = Substitute.For<ISnapshotRepository>();
        failing.TryLeaseInMemoryState(default, default, out _).ThrowsForAnyArgs(new IOException("disk failure"));

        // Matches HistoryWriter.MaxConsecutiveCaptureFailures.
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

    // The per-block marker binds the block's state root; a query for the same height with a different root (a
    // non-canonical EIP-1898 hash) must not be served.
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

    // A windowed writer must declare itself v3 from its very first capture and stay there: if MarkBlock instead
    // wrote the plain FormatVersion on every captured block, the very first capture after a floor publish would
    // silently downgrade the stamp back to 2, and an older floor-unaware binary reading a pruned DB would pass
    // VerifyFormat and serve absent instead of erroring for every pruned height.
    [Test]
    public void Windowed_writer_stamps_windowed_format_version_and_it_survives_further_captures()
    {
        (HistoryWriter windowed, _) = CreateWindowedPair(retentionBlocks: 100);
        windowed.SeedGenesis([], StateAt(0).StateRoot);

        Assert.That(HistoryColumnsWriter.GetStampedFormatVersion(_historyColumns), Is.EqualTo((byte?)3),
            "precondition: a windowed writer's seed must stamp the windowed format version");

        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 1))]);
        windowed.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);
        CommitBlock(1, 2, accountChanges: [(AddrA, new Account(2, 2))]);
        windowed.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        Assert.That(HistoryColumnsWriter.GetStampedFormatVersion(_historyColumns), Is.EqualTo((byte?)3),
            "further captures on a windowed writer must never regress the stamp back to the plain format version");
    }

    [Test]
    public void Unwindowed_writer_stamps_the_plain_format_version()
    {
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 1))]);
        _writer.SeedGenesis([], StateAt(0).StateRoot);
        _writer.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        Assert.That(HistoryColumnsWriter.GetStampedFormatVersion(_historyColumns), Is.EqualTo((byte?)2),
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

    // SeedPivot on a v3 (windowed) writer publishes the marker/watermark/floor and writes no rows — reads at the
    // pivot must resolve via HistoryStoreV3's persisted-flat fallback (the live Account/Storage columns, already
    // populated as if sync had just finished writing them), not via any captured row.
    [Test]
    public void SeedPivot_OnAWindowedWriter_PublishesWatermarkAndFloor_AndReadsFallThroughToThePersistedFlatColumns()
    {
        // Construct the writer/reader before populating the live flat columns, matching real startup order (DI
        // constructs both before sync ever runs) — ResolveSlotEncoding reads whether the storage column is empty
        // at construction time, so writing to it first would flip the resolved encoding out from under this test.
        (HistoryWriter windowedWriter, HistoryReader windowedReader) = CreateWindowedPair(retentionBlocks: 1000);

        _db.GetColumnDb(FlatDbColumns.Account).PutSpan(AccountKey(AddrA), EncodedAccount(new Account(5, 500)));
        Span<byte> slotValueBuffer = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        int slotValueLength = BaseFlatPersistence.EncodeSlotValue(SlotValue.FromSpanWithoutLeadingZero([0xAA]), RlpWrapSlots, slotValueBuffer);
        _db.GetColumnDb(FlatDbColumns.Storage).PutSpan(StorageKey(AddrA, Slot1), slotValueBuffer[..slotValueLength]);

        StateId pivot = StateAt(100);

        windowedWriter.SeedPivot(100, pivot.StateRoot, Substitute.For<IPersistence.IPersistenceReader>());

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

    // The invariant chain that makes the v3 persisted-flat fallback sound, exercised directly: capture (which
    // records the pre-value row for the just-captured change) always completes strictly before the corresponding
    // flat persist commits. A read for a block at or before the just-published watermark, whose forward-seek
    // finds no captured change above it, must resolve via the persisted flat column — which, at the moment
    // capture finishes, still reflects the state from BEFORE this round (the old watermark), and after the round
    // (once persist catches up) reflects the state that round captured. Both moments must read correctly.
    [Test]
    public void V3Read_AtOrBelowWatermark_ResolvesCorrectly_BeforeAndAfterThePersistCatchesUp()
    {
        (HistoryWriter windowedWriter, HistoryReader windowedReader) = CreateWindowedPair(retentionBlocks: 1000);

        // "Persisted flat, as of the old watermark (block 0)": AddrA's value before this round's only change.
        _db.GetColumnDb(FlatDbColumns.Account).PutSpan(AccountKey(AddrA), EncodedAccount(new Account(0, 100)));
        windowedWriter.SeedGenesis([], StateAt(0).StateRoot);

        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 111))]);
        windowedWriter.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        // Capture has published watermark = 1 and recorded the pre-value row, but — matching the real ordering
        // ("the flat persist commits only after") — the persisted flat column has NOT been updated yet: it is
        // deliberately left showing block 0's value at this point in the test.
        bool foundAtZero = windowedReader.TryGetAccount(0, AddrA, out AccountStruct atZero);

        // Now simulate the flat persist catching up to what this round captured.
        _db.GetColumnDb(FlatDbColumns.Account).PutSpan(AccountKey(AddrA), EncodedAccount(new Account(1, 111)));

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
        // Pivot-start is v3-only: calling it on the class's default (unwindowed) writer must not publish a floor
        // it cannot back with either captured rows or a fallback.
        _writer.SeedPivot(100, StateAt(100).StateRoot, Substitute.For<IPersistence.IPersistenceReader>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_writer.LastCapturedBlock, Is.EqualTo(0UL));
            Assert.That(_reader.IsPrunedBelowFloor(50), Is.False);
        }
    }

    // v3 only: a self-destruct with persisted storage must materialize a per-slot pre-value row by enumerating
    // the persisted flat column at capture time (before that round's persist commits the range-delete) — without
    // this, a v3 read below the destruct block would find no captured row and fall through to a persisted column
    // that (once caught up) no longer has the slot, silently reporting absent instead of the true pre-destruct
    // value.
    [Test]
    public void V3_SelfDestruct_MaterializesPerSlotPreValue_ReadableBelowDestructBlock()
    {
        (HistoryWriter windowedWriter, HistoryReader windowedReader) = CreateWindowedPair(retentionBlocks: 1000);

        windowedWriter.SeedGenesis([], StateAt(0).StateRoot);

        CommitBlock(0, 1, storageChanges: [(AddrA, Slot1, HistorySlot(0x0a))]);
        windowedWriter.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        // Simulate the round-1 persist catching up before round 2 (the destruct) captures - matching the real
        // per-round capture-then-persist ordering, so the destruct's enumeration sees the true pre-destruct value.
        _db.GetColumnDb(FlatDbColumns.Storage).PutSpan(StorageKey(AddrA, Slot1), EncodedHistorySlot(0x0a));

        CommitBlock(1, 2, accountChanges: [(AddrA, null)], selfDestructs: [(AddrA, false)]);
        windowedWriter.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        bool foundBelow = windowedReader.TryGetStorage(1, AddrA, Slot1, out SlotValue belowDestruct);

        // Simulate the destruct's own persist (range-delete) catching up.
        _db.GetColumnDb(FlatDbColumns.Storage).Remove(StorageKey(AddrA, Slot1));

        bool foundAt = windowedReader.TryGetStorage(2, AddrA, Slot1, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(foundBelow, Is.True, "the slot's pre-destruct value must be readable strictly below the destruct block");
            Assert.That(belowDestruct.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0x0a }));
            Assert.That(foundAt, Is.False, "the slot must read empty at/after the destruct once the persist has caught up");
        }
    }

    // Both an account and its multiple slots created, then destructed, within the same retention window: the
    // destruct's enumeration must recover every persisted slot (not just ones explicitly touched at the destruct's
    // own block), and the account's own tombstone must resolve independently of the slot handling.
    [Test]
    public void V3_AccountCreatedThenDestructed_AllSlotsResolveCorrectlyAcrossTheWindow()
    {
        (HistoryWriter windowedWriter, HistoryReader windowedReader) = CreateWindowedPair(retentionBlocks: 1000);

        windowedWriter.SeedGenesis([], StateAt(0).StateRoot);

        CommitBlock(0, 1,
            accountChanges: [(AddrA, new Account(1, 100))],
            storageChanges: [(AddrA, Slot1, HistorySlot(0x0a)), (AddrA, Slot2, HistorySlot(0x0b))]);
        windowedWriter.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        _db.GetColumnDb(FlatDbColumns.Account).PutSpan(AccountKey(AddrA), EncodedAccount(new Account(1, 100)));
        _db.GetColumnDb(FlatDbColumns.Storage).PutSpan(StorageKey(AddrA, Slot1), EncodedHistorySlot(0x0a));
        _db.GetColumnDb(FlatDbColumns.Storage).PutSpan(StorageKey(AddrA, Slot2), EncodedHistorySlot(0x0b));

        CommitBlock(1, 2, accountChanges: [(AddrA, null)], selfDestructs: [(AddrA, false)]);
        windowedWriter.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        bool foundAccountBelow = windowedReader.TryGetAccount(1, AddrA, out AccountStruct accountBelow);
        bool foundSlot1Below = windowedReader.TryGetStorage(1, AddrA, Slot1, out SlotValue slot1Below);
        bool foundSlot2Below = windowedReader.TryGetStorage(1, AddrA, Slot2, out SlotValue slot2Below);

        // Simulate the destruct's own persist (account tombstone + storage range-delete) catching up.
        _db.GetColumnDb(FlatDbColumns.Account).Remove(AccountKey(AddrA));
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

    // A destruct and a same-block rewrite of the same slot (resurrection): the live column nets out to just the
    // rewrite (destruct-then-write batch order), and the synthetic destruct-wipe touch must neither corrupt, nor
    // be corrupted by, the rewrite's own touch (PendingV3Writes.ResolveAndTrack's same-block guard).
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

        // Simulate the flat persist catching up to round 2: the destruct's range-delete and the rewrite net out
        // to just "slot1 = 0x0b" in the live column.
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

    // Above HistoryWriter's per-destruct slot enumeration cap, no per-slot pre-value rows are written at all - a
    // poison marker is recorded instead, and the reader must fail closed for this account below the destruct
    // block rather than silently falling through and reporting every slot absent.
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

        Assert.That(() => windowedReader.TryGetStorage(0, AddrA, Slot1, out _),
            Throws.InstanceOf<InvalidOperationException>(),
            "reads for this account below an over-cap destruct must fail closed rather than silently report every slot absent");
    }

    // A key with no older touch anywhere in the walk has its pre-value known only once ResolvePendingV3 reads the
    // persisted column, at the very end of the walk - the exact reason the sidecar entry cannot be written
    // immediately per-block and must be buffered until then (see HistoryWriter.FlushSidecarBuilders' remarks).
    [Test]
    public void V3_SidecarEntry_ForATouchWithNoOlderTouchInTheWalk_ResolvesPreValueFromThePersistedColumn()
    {
        HistoryWriter windowedWriter = CreateWindowedWriterWithSidecar(retentionBlocks: 1000);

        _db.GetColumnDb(FlatDbColumns.Account).PutSpan(AccountKey(AddrA), EncodedAccount(new Account(0, 100)));
        windowedWriter.SeedGenesis([], StateAt(0).StateRoot);

        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 111))]);
        windowedWriter.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        ChangesetAccountEntry entry = DecodeSidecarEntries(1)[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.Address, Is.EqualTo(AddrA));
            Assert.That(entry.AccountValue.ToArray(), Is.EqualTo(EncodedAccount(new Account(1, 111))),
                "the post-value must still be the block's own touch - unaffected by adding PreValue");
            Assert.That(entry.AccountPreValue.ToArray(), Is.EqualTo(EncodedAccount(new Account(0, 100))),
                "the pre-value must be exactly what the persisted column held before this round - the same read ResolvePendingV3 uses for the history row itself");
        }
    }

    // Two blocks touching the same key: the older block's post-value becomes the newer block's resolved
    // pre-value via in-walk chaining (PendingV3Writes.ResolveAndTrack), never the persisted-column fallback.
    [Test]
    public void V3_SidecarEntry_ForAChainedTouch_ResolvesPreValueFromTheOlderTouchInTheSameWalk()
    {
        HistoryWriter windowedWriter = CreateWindowedWriterWithSidecar(retentionBlocks: 1000);
        windowedWriter.SeedGenesis([], StateAt(0).StateRoot);

        CommitBlock(0, 1, storageChanges: [(AddrA, Slot1, HistorySlot(0x0a))]);
        CommitBlock(1, 2, storageChanges: [(AddrA, Slot1, HistorySlot(0x0b))]);
        windowedWriter.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        ChangesetSlotEntry block1Slot = DecodeSidecarEntries(1)[0].StorageChanges[0];
        ChangesetSlotEntry block2Slot = DecodeSidecarEntries(2)[0].StorageChanges[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block1Slot.PreValue.Length, Is.EqualTo(0), "block 1 is the slot's first-ever touch - no prior value exists");
            Assert.That(block2Slot.PreValue.ToArray(), Is.EqualTo(EncodedHistorySlot(0x0a)),
                "block 2's pre-value must be block 1's post-value, resolved by in-walk chaining, not a persisted-column read");
        }
    }

    // A same-block destruct + rewrite of the same slot: the destruct's synthetic wipe has no sidecar entry of its
    // own (a separate, documented gap - see HandleSelfDestructV3's remarks), so the rewrite's real sidecar entry
    // must still resolve its pre-value correctly, unclobbered by the synthetic touch it shares a block with.
    [Test]
    public void V3_SidecarEntry_ForASameBlockDestructAndRewrite_ResolvesTheRewriteEntrysPreValueCorrectly()
    {
        HistoryWriter windowedWriter = CreateWindowedWriterWithSidecar(retentionBlocks: 1000);
        windowedWriter.SeedGenesis([], StateAt(0).StateRoot);

        CommitBlock(0, 1, storageChanges: [(AddrA, Slot1, HistorySlot(0x0a))]);
        windowedWriter.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);
        _db.GetColumnDb(FlatDbColumns.Storage).PutSpan(StorageKey(AddrA, Slot1), EncodedHistorySlot(0x0a));

        CommitBlock(1, 2, storageChanges: [(AddrA, Slot1, HistorySlot(0x0b))], selfDestructs: [(AddrA, false)]);
        windowedWriter.CaptureUpTo(StateAt(2), _repository, CancellationToken.None);

        List<ChangesetAccountEntry> block2Entries = DecodeSidecarEntries(2);
        ChangesetSlotEntry rewrittenSlot = block2Entries.Single(e => e.Address == AddrA).StorageChanges.Single(s => s.Slot == Slot1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rewrittenSlot.Value.ToArray(), Is.EqualTo(EncodedHistorySlot(0x0b)), "the rewrite's post-value must win, matching the live column");
            Assert.That(rewrittenSlot.PreValue.ToArray(), Is.EqualTo(EncodedHistorySlot(0x0a)), "the rewrite entry's own pre-value must resolve correctly despite sharing its block with the destruct's sinkless synthetic touch");
        }
    }

    // v2 (unwindowed) capture has no deferred pre-value mechanism at all - its sidecar entries (a documented gap,
    // see RecordChangesetSidecarChunk's remarks) must round-trip with an explicitly empty PreValue, never a wrong
    // guess, so a consumer can tell "genuinely created here" from "not derived" only by cross-referencing the
    // stamped format version - never by the PreValue field's shape alone.
    [Test]
    public void V2_SidecarEntry_HasNoPreValueMechanism_RoundTripsEmpty()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryChangesetSidecarEnabled = true };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        HistoryWriter unwindowedWriter = new(_db, _historyColumns, config, availability, rowFormat, LimboLogs.Instance);

        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 100))]);
        unwindowedWriter.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        ChangesetAccountEntry entry = DecodeSidecarEntries(1)[0];
        Assert.That(entry.AccountPreValue.Length, Is.EqualTo(0));
    }

    private List<ChangesetAccountEntry> DecodeSidecarEntries(ulong block)
    {
        ChangesetSidecarStore sidecarStore = new(_historyColumns.GetColumnDb(FlatHistoryColumns.ChangesetSidecar));
        byte[]? chunk = sidecarStore.TryGetChunk(block, 0);
        Assert.That(chunk, Is.Not.Null, $"precondition: block {block} must have a recorded sidecar chunk");
        return ChangesetChunkCodec.Decode(chunk!);
    }

    private HistoryWriter CreateWindowedWriterWithSidecar(ulong retentionBlocks)
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetentionBlocks = retentionBlocks, HistoryChangesetSidecarEnabled = true };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        return new HistoryWriter(_db, _historyColumns, config, availability, rowFormat, LimboLogs.Instance);
    }

    // The range mixes tiers deliberately — blocks 2-3 converted to the persisted tier, blocks 1 and 4 in memory —
    // and block 2 also deletes AddrB, so the account tombstone round-trips through the persisted format.
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
        const int compactSize = 8;
        const int blockCount = 24; // 3 full compaction windows at CompactSize 8.

        FlatDbConfig compactionConfig = new() { CompactSize = compactSize, CompactionOffset = 0 };
        CompactionSchedule schedule = new(new MemDb(), compactionConfig, LimboLogs.Instance);
        SnapshotCompactor compactor = new(compactionConfig, schedule, _resourcePool, _repository, LimboLogs.Instance);

        // Each block gives the account a unique end-of-block value (nonce == balance == block) and a unique slot
        // value, so a gap that resolves to an earlier compaction boundary is detectable.
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

    // Mirrors the tip path of FlatDbManager.GatherReadOnlySnapshotBundle: assemble the live per-block snapshots from
    // the read block down to the persisted floor (block 0), then read through them.
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

    // First byte is always non-zero so the value survives the without-leading-zeros slot roundtrip unchanged.
    private static byte[] CompactionSlotBytes(ulong block) => [0xAB, (byte)(block >> 8), (byte)block];

    private static SlotValue CompactionSlotFor(ulong block) => SlotValue.FromSpanWithoutLeadingZero(CompactionSlotBytes(block));

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

    // Establishes the block-0 watermark floor (as production genesis capture / SeedGenesis does) so a later capture
    // walk connects to it and publishes its watermark without needing a genesis snapshot in the repository.
    private void SeedGenesisFloor() => _writer.SeedGenesis([], StateAt(0).StateRoot);

    // A windowed writer/reader pair sharing one HistoryAvailability/HistoryRowFormat resolved from one config -
    // mirroring the single DI-bound instance production wires both through, matching every collaborator to the
    // same config the pruner's own tests must also share (see HistoryColumnsWriter.CreateSharedFormat's remarks).
    private (HistoryWriter Writer, HistoryReader Reader) CreateWindowedPair(ulong retentionBlocks)
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetentionBlocks = retentionBlocks };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        HistoryWriter writer = new(_db, _historyColumns, config, availability, rowFormat, LimboLogs.Instance);
        HistoryReader reader = new(_db, _historyColumns, config, availability, rowFormat, LimboLogs.Instance);
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

    // Right-aligned (numeric) slot value, matching what the reader decodes; Slot() is the raw 32-byte layout.
    private static SlotValue HistorySlot(params byte[] bytes) => SlotValue.FromSpanWithoutLeadingZero(bytes);

    // The flat-column-encoded bytes for a right-aligned (numeric) slot value - for simulating what the live flat
    // Storage column would hold for a value written via HistorySlot(), when a test pre-populates it directly.
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
