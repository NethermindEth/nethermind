// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        _writer = new HistoryWriter(_db, _historyColumns, new FlatDbConfig { HistoryEnabled = true }, LimboLogs.Instance);
        _reader = new HistoryReader(_db, _historyColumns, LimboLogs.Instance);
        _accountHistory = new HistoryStore(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory));
        _storageHistory = new HistoryStore(_historyColumns.GetColumnDb(FlatHistoryColumns.StorageHistory));
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
        HistoryWriter restarted = new(_db, _historyColumns, new FlatDbConfig { HistoryEnabled = true }, LimboLogs.Instance);
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

        Assert.DoesNotThrow(() => _ = new HistoryReader(_db, _historyColumns, LimboLogs.Instance));
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
        HistoryWriter disabled = new(_db, _historyColumns, new FlatDbConfig { HistoryEnabled = false }, LimboLogs.Instance);
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 100))]);

        disabled.CaptureUpTo(StateAt(1), _repository, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_reader.HasHistoryForBlock(1), Is.False);
            Assert.That(_reader.TryGetAccount(1, AddrA, out _), Is.False);
        }
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

        Assert.DoesNotThrow(() => _writer.CaptureUpTo(StateAt(blockCount), _repository));

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

    public enum ExpectedKind
    {
        Absent,
        Tombstone,
        Value
    }
}
