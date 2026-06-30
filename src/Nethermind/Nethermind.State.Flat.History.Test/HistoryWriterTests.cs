// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        _repository = new SnapshotRepository(LimboLogs.Instance);
        _writer = new HistoryWriter(_db, _historyColumns, new FlatDbConfig { HistoryEnabled = true }, LimboLogs.Instance);
        _reader = new HistoryReader(_db, _historyColumns, LimboLogs.Instance);
        _accountHistory = new HistoryStore(
            _historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory),
            _historyColumns.GetColumnDb(FlatHistoryColumns.AccountChangeSets));
        _storageHistory = new HistoryStore(
            _historyColumns.GetColumnDb(FlatHistoryColumns.StorageHistory),
            _historyColumns.GetColumnDb(FlatHistoryColumns.StorageChangeSets));
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _historyColumns.Dispose();
    }

    // AddrA: (nonce 1, balance 100) @ b1, overwritten to (nonce 2, balance 200) @ b2, deleted @ b3.
    // Nonce == block number for the committed values, so the expected account is reconstructible from readBlock.
    [TestCase(0ul, 0ul, ExpectedKind.Absent)]   // before the first change -> caller falls back to the tip
    [TestCase(1ul, 100ul, ExpectedKind.Value)]
    [TestCase(2ul, 200ul, ExpectedKind.Value)]
    [TestCase(3ul, 0ul, ExpectedKind.Tombstone)] // deleted
    [TestCase(4ul, 0ul, ExpectedKind.Tombstone)]
    public void Captures_account_value_as_of_block(ulong readBlock, ulong balance, ExpectedKind kind)
    {
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(1, 100))]);
        CommitBlock(1, 2, accountChanges: [(AddrA, new Account(2, 200))]);
        CommitBlock(2, 3, accountChanges: [(AddrA, null)]);

        _writer.CaptureUpTo(StateAt(3), _repository);

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

        _writer.CaptureUpTo(StateAt(3), _repository);

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

        _writer.CaptureUpTo(StateAt(1), _repository);

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
        Account atBlock1 = new(1, 11);
        Account atBlock2 = new(2, 22);
        CommitBlock(0, 1, accountChanges: [(AddrA, atBlock1)]);
        _writer.CaptureUpTo(StateAt(1), _repository);

        CommitBlock(1, 2, accountChanges: [(AddrA, atBlock2)]);
        _writer.CaptureUpTo(StateAt(2), _repository);

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

        _writer.CaptureUpTo(StateAt(3), _repository);

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

    // slot written @1, cleared @2 (null tombstone), re-written @3.
    [TestCase(0ul, null)]
    [TestCase(1ul, "0a")]
    [TestCase(2ul, null)] // cleared -> tombstone -> absent
    [TestCase(3ul, "0c")]
    [TestCase(4ul, "0c")]
    public void Storage_cleared_then_rewritten_reads_per_height(ulong readBlock, string? expectedHex)
    {
        CommitBlock(0, 1, storageChanges: [(AddrA, Slot1, SlotValue.FromSpanWithoutLeadingZero([0x0a]))]);
        CommitBlock(1, 2, storageChanges: [(AddrA, Slot1, null)]);
        CommitBlock(2, 3, storageChanges: [(AddrA, Slot1, SlotValue.FromSpanWithoutLeadingZero([0x0c]))]);

        _writer.CaptureUpTo(StateAt(3), _repository);

        bool found = _reader.TryGetStorage(readBlock, AddrA, Slot1, out SlotValue value);

        using (Assert.EnterMultipleScope())
        {
            if (expectedHex is null)
            {
                Assert.That(found, Is.False);
            }
            else
            {
                Assert.That(found, Is.True);
                Assert.That(value.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(Convert.FromHexString(expectedHex)));
            }
        }
    }

    // an EIP-158-style empty account (nonce 0, balance 0) must round-trip as a
    // present account, not as a deletion tombstone.
    [Test]
    public void Empty_account_round_trips_as_present_not_tombstone()
    {
        CommitBlock(0, 1, accountChanges: [(AddrA, new Account(0UL, UInt256.Zero))]);

        _writer.CaptureUpTo(StateAt(1), _repository);

        bool found = _reader.TryGetAccount(1, AddrA, out AccountStruct account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(account.Nonce, Is.EqualTo(0UL));
            Assert.That(account.Balance, Is.EqualTo(UInt256.Zero));
        }
    }

    // turning HistoryEnabled on is additive. The live tip read through a
    // real ReadOnlySnapshotBundle must still return the latest value while capture also populates the history index.
    [Test]
    public void Flag_on_keeps_tip_reads_correct_and_populates_history()
    {
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

    // per-block history capture must survive REAL compaction with no
    // gaps. The compactor (and its schedule) is built locally so the shared [SetUp] stays unchanged; it must still
    // call the real DoCompactSnapshot and assert compaction actually fired before checking every block.
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

        _writer.CaptureUpTo(StateAt(blockCount), _repository);

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
        SnapshotPooledList snapshots = _repository.AssembleSnapshots(StateAt(tip), StateAt(0), estimatedSize);
        return new ReadOnlySnapshotBundle(snapshots, new NoopPersistenceReader(), recordDetailedMetrics: false);
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
        (Address Address, UInt256 Slot, SlotValue? Value)[]? storageChanges = null)
    {
        Snapshot snapshot = _resourcePool.CreateSnapshot(StateAt(fromBlock), StateAt(toBlock), ResourcePool.Usage.ReadOnlyProcessingEnv);

        if (accountChanges is not null)
            foreach ((Address address, Account? account) in accountChanges)
                snapshot.Content.Accounts[address] = account;

        if (storageChanges is not null)
            foreach ((Address address, UInt256 slot, SlotValue? value) in storageChanges)
                snapshot.Content.Storages[(address, slot)] = value;

        Assert.That(_repository.TryAddSnapshot(snapshot), Is.True);
        _repository.AddStateId(StateAt(toBlock));
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

    public enum ExpectedKind
    {
        Absent,
        Tombstone,
        Value
    }
}
