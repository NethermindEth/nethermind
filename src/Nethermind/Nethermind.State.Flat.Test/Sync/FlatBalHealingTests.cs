// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.Sync;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Healing;
using Nethermind.Synchronization.FastSync;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync;

[TestFixture]
public class FlatBalHealingTests
{
    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private RocksDbPersistence _persistence = null!;
    private TrieReassembler _reassembler = null!;
    private TestMemDb _codeDb = null!;
    private MemDb _balDb = null!;
    private BlockAccessListStore _balStore = null!;
    private IBlockTree _blockTree = null!;
    private ITreeSyncStore _syncStore = null!;
    private ICodeRecovery _codeRecovery = null!;
    private FlatBalHealing _healing = null!;

    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _persistence = new RocksDbPersistence(_columnsDb, LimboLogs.Instance, NullTrieNodeLog.Instance);
        _reassembler = new TrieReassembler(_persistence, LimboLogs.Instance);
        _codeDb = new TestMemDb();
        _balDb = new MemDb();
        _balStore = new BlockAccessListStore(_balDb);
        _blockTree = Substitute.For<IBlockTree>();
        _syncStore = Substitute.For<ITreeSyncStore>();
        _codeRecovery = Substitute.For<ICodeRecovery>();
        _healing = new(_blockTree, _balStore, _reassembler, _persistence, _syncStore, _codeDb, new Lazy<ICodeRecovery>(_codeRecovery), LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _columnsDb.Dispose();
        _codeDb.Dispose();
        _balDb.Dispose();
    }

    [Test]
    public async Task Heals_when_bals_reach_target_root()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100), Acc(TestItem.AddressB, 200));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressA, 150), Acc(TestItem.AddressB, 200));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(firstPivot, expected, BalanceBal(TestItem.AddressA, 150));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [], default);

        Assert.That(result, Is.True);
        _syncStore.Received(1).FinalizeSync(lastPivot);
    }

    [Test]
    public async Task Heals_across_multiple_chunks()
    {
        // 260 blocks span several BalsChunkSize chunks: each chunk must see the previous one's committed state.
        SeedInitialState(Acc(TestItem.AddressA, 100), Acc(TestItem.AddressB, 200));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressA, 1000), Acc(TestItem.AddressB, 2000));

        BlockHeader firstPivot = Pivot(0, TestItem.KeccakA);
        BlockHeader lastPivot = firstPivot;
        for (ulong number = 1; number <= 260; number++)
        {
            ReadOnlyBlockAccessList bal = number switch
            {
                1 => BalanceBal(TestItem.AddressA, 1000),
                260 => BalanceBal(TestItem.AddressB, 2000),
                _ => EmptyBal()
            };
            lastPivot = SetupBlock(lastPivot, number == 260 ? expected : TestItem.KeccakA, bal);
        }

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [], default);

        Assert.That(result, Is.True);
        _syncStore.Received(1).FinalizeSync(lastPivot);
    }

    [Test]
    public async Task Heals_when_same_account_storage_changes_across_chunks()
    {
        // Slot 1 changes in the first chunk (block 1), slot 2 in a later one (block 300). The later chunk must
        // build the storage tree on the storage root committed by the first (read via reader.GetAccount from
        // the flat store), not the stale root from before it — otherwise slot 1's update is lost.
        SeedInitialState(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05])]));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x09]), new Slot(2, [0x07])]));

        BlockHeader firstPivot = Pivot(0, TestItem.KeccakA);
        BlockHeader lastPivot = firstPivot;
        for (ulong number = 1; number <= 300; number++)
        {
            ReadOnlyBlockAccessList bal = number switch
            {
                1 => StorageBal(TestItem.AddressA, 1, 9),
                300 => StorageBal(TestItem.AddressA, 2, 7),
                _ => EmptyBal()
            };
            lastPivot = SetupBlock(lastPivot, number == 300 ? expected : TestItem.KeccakA, bal);
        }

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [StorageOf(TestItem.AddressA)], default);

        Assert.That(result, Is.True);
        _syncStore.Received(1).FinalizeSync(lastPivot);
    }

    [Test]
    public async Task Heals_when_bal_creates_account_with_code()
    {
        byte[] code = [0x60, 0x00, 0x60, 0x00];
        Hash256 codeHash = Keccak.Compute(code);

        SeedInitialState(Acc(TestItem.AddressA, 100));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressA, 100), Acc(TestItem.AddressB, 500, code: code));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        ReadOnlyAccountChanges changes = Build.An.AccountChanges
            .WithAddress(TestItem.AddressB)
            .WithBalanceChanges(new BalanceChange(0, 500))
            .WithCodeChanges(new CodeChange(0, code))
            .TestObject;
        BlockHeader lastPivot = SetupBlock(firstPivot, expected, Bal(changes));
        Assert.That(_codeDb.Get(codeHash.Bytes), Is.Null, "the code must reach the db through healing, not through the setup");

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [], default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True);
            Assert.That(_codeDb.Get(codeHash.Bytes), Is.EqualTo(code));
        }
    }

    // EIP-8298: an adoption records only a hash; its bytecode comes from the change that deposited it, even when
    // the depositor's final code differs.
    [Test]
    public async Task Heals_when_bal_adopts_code_its_depositor_replaces()
    {
        byte[] code = [0x60, 0x00, 0x60, 0x00];
        byte[] replacement = [0x60, 0x01];
        Hash256 codeHash = Keccak.Compute(code);

        SeedInitialState(Acc(TestItem.AddressA, 100));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressA, 100, code: code), Acc(TestItem.AddressB, 500, code: replacement));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        ReadOnlyAccountChanges adopter = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithCodeChanges(CodeChange.Adopted(2, codeHash.ValueHash256))
            .TestObject;
        ReadOnlyAccountChanges depositor = Build.An.AccountChanges
            .WithAddress(TestItem.AddressB)
            .WithBalanceChanges(new BalanceChange(1, 500))
            .WithCodeChanges(new CodeChange(1, code), new CodeChange(3, replacement))
            .TestObject;
        BlockHeader lastPivot = SetupBlock(firstPivot, expected, Bal(adopter, depositor));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [], default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True, "healed");
            Assert.That(_codeDb.Get(codeHash.Bytes), Is.EqualTo(code), "adopted code");
            Assert.That(_codeDb.Get(Keccak.Compute(replacement).Bytes), Is.EqualTo(replacement), "replaced code");
        }
    }

    // EIP-8298: snap downloaded the source after it replaced its code, so neither snap nor the BALs supply the
    // bytecode the adopter took; healing recovers it by hash before finalizing.
    [Test]
    public async Task Recovers_adopted_code_whose_source_has_since_replaced_it([Values] bool peerServesCode)
    {
        byte[] code = [0x60, 0x00, 0x60, 0x00];
        byte[] replacement = [0x60, 0x01];
        Hash256 codeHash = Keccak.Compute(code);
        Hash256 replacementHash = Keccak.Compute(replacement);

        SeedInitialState(Acc(TestItem.AddressA, 100), Acc(TestItem.AddressB, 500, code: replacement));
        _codeDb.Set(replacementHash.Bytes, replacement);
        _codeRecovery.Recover(codeHash.ValueHash256, Arg.Any<CancellationToken>()).Returns(peerServesCode ? code : null);
        Hash256 expected = BuildRoot(Acc(TestItem.AddressA, 100, code: code), Acc(TestItem.AddressB, 500, code: replacement));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        ReadOnlyAccountChanges adopter = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithCodeChanges(CodeChange.Adopted(1, codeHash.ValueHash256))
            .TestObject;
        ReadOnlyAccountChanges source = Build.An.AccountChanges
            .WithAddress(TestItem.AddressB)
            .WithCodeChanges(CodeChange.Adopted(2, replacementHash.ValueHash256))
            .TestObject;
        BlockHeader lastPivot = SetupBlock(firstPivot, expected, Bal(adopter, source));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [], default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(peerServesCode), "finalized");
            Assert.That(_codeDb.Get(codeHash.Bytes), peerServesCode ? Is.EqualTo(code) : Is.Null, "adopted code");
            _ = _codeRecovery.Received(1).Recover(codeHash.ValueHash256, Arg.Any<CancellationToken>());
            _ = _codeRecovery.DidNotReceive().Recover(replacementHash.ValueHash256, Arg.Any<CancellationToken>());
            _syncStore.Received(peerServesCode ? 1 : 0).FinalizeSync(lastPivot);
        }
    }

    [Test]
    public async Task Heals_when_bal_updates_storage()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05])]));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x09])]));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        ReadOnlyAccountChanges changes = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithStorageChanges(1, new StorageChange(0, (UInt256)9))
            .TestObject;
        BlockHeader lastPivot = SetupBlock(firstPivot, expected, Bal(changes));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [StorageOf(TestItem.AddressA)], default);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task Heals_when_bal_clears_storage_slot()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05])]));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressA, 100));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        ReadOnlyAccountChanges changes = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithStorageChanges(1, new StorageChange(0, UInt256.Zero))
            .TestObject;
        BlockHeader lastPivot = SetupBlock(firstPivot, expected, Bal(changes));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [StorageOf(TestItem.AddressA)], default);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task Heals_when_bal_empties_account()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100), Acc(TestItem.AddressB, 200));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressB, 200));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(firstPivot, expected, BalanceBal(TestItem.AddressA, 0));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [], default);

        Assert.That(result, Is.True);
        _syncStore.Received(1).FinalizeSync(lastPivot);
    }

    [Test]
    public async Task Emptied_account_that_still_has_storage_is_removed_from_state()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05])]), Acc(TestItem.AddressB, 200));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressB, 200));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(firstPivot, expected, BalanceBal(TestItem.AddressA, 0));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [StorageOf(TestItem.AddressA)], default);

        Assert.That(result, Is.True);
        _syncStore.Received(1).FinalizeSync(lastPivot);
    }

    public static IEnumerable<TestCaseData> RevivalCases()
    {
        (string Name, BalanceChange[] Changes, uint ReviveIndex, int EmptyBlocks, bool SeparateDeletion)[] scenarios =
        [
            ("transaction", [new(1, 0), new(2, 100)], 2, 0, false),
            ("repeated transaction", [new(1, 0), new(2, 100), new(3, 0), new(4, 100)], 4, 0, false),
            ("block", [new(1, 100)], 1, 0, true),
            ("chunk", [new(1, 100)], 1, 15, true)
        ];
        foreach ((string name, BalanceChange[] changes, uint reviveIndex, int emptyBlocks, bool separateDeletion) in scenarios)
        {
            foreach (bool writesStorageAfterRevival in new[] { false, true })
                yield return new TestCaseData(changes, reviveIndex, emptyBlocks, separateDeletion, writesStorageAfterRevival)
                    .SetName($"Revived account loses old storage across {name}, new storage: {writesStorageAfterRevival}");
        }
    }

    [TestCaseSource(nameof(RevivalCases))]
    public async Task Revived_account_loses_its_old_storage(
        BalanceChange[] balanceChanges, uint reviveIndex, int emptyBlocks, bool separateDeletion, bool writesStorageAfterRevival)
    {
        // A code-less, nonce-zero payer with positive balance and storage is a valid nonempty prestate.
        SeedInitialState(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05])]), Acc(TestItem.AddressB, 200));
        byte[]? code = writesStorageAfterRevival ? [0x00] : null;
        Hash256 expected = BuildRoot(
            Acc(TestItem.AddressA, 100, nonce: writesStorageAfterRevival ? 1UL : 0, code: code,
                slots: writesStorageAfterRevival ? [new Slot(2, [0x07])] : []),
            Acc(TestItem.AddressB, 200));

        BlockHeader firstPivot = Pivot(0, TestItem.KeccakA);
        BlockHeader lastPivot = firstPivot;
        for (int i = 0; i < emptyBlocks; i++) lastPivot = SetupBlock(lastPivot, TestItem.KeccakA, EmptyBal());
        if (separateDeletion)
            lastPivot = SetupBlock(lastPivot, TestItem.KeccakA, BalanceBal(TestItem.AddressA, 0));

        ReadOnlyAccountChanges changes = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges(balanceChanges)
            .WithNonceChanges(writesStorageAfterRevival ? [new NonceChange(reviveIndex, 1)] : [])
            .WithCodeChanges(writesStorageAfterRevival ? [new CodeChange(reviveIndex, code!)] : [])
            .TestObject;
        if (writesStorageAfterRevival)
            changes = new ReadOnlyAccountChanges(changes.Address,
                [new ReadOnlySlotChanges(2, [new StorageChange(reviveIndex + 1, 7)])], [],
                changes.BalanceChanges, changes.NonceChanges, changes.CodeChanges);
        lastPivot = SetupBlock(lastPivot, expected, Bal(changes));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [StorageOf(TestItem.AddressA)], default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True, "healing must reach the root after deletion and revival");
            Assert.That(FlatSlotExists(TestItem.AddressA, 1), Is.False, "old storage must be wiped");
            Assert.That(FlatSlotExists(TestItem.AddressA, 2), Is.EqualTo(writesStorageAfterRevival));
        }
    }

    [Test]
    public async Task Changes_at_the_same_transaction_index_do_not_delete_a_nonempty_account()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05])]));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressA, 0, nonce: 1, slots: [new Slot(1, [0x05])]));
        BlockHeader firstPivot = Pivot(0, TestItem.KeccakA);
        ReadOnlyAccountChanges changes = Build.An.AccountChanges.WithAddress(TestItem.AddressA)
            .WithBalanceChanges(new BalanceChange(1, 0))
            .WithNonceChanges(new NonceChange(1, 1)).TestObject;
        BlockHeader lastPivot = SetupBlock(firstPivot, expected, Bal(changes));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [StorageOf(TestItem.AddressA)], default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True);
            Assert.That(FlatSlotExists(TestItem.AddressA, 1), Is.True);
        }
    }

    [Test]
    public async Task Read_only_empty_account_is_preserved_during_healing()
    {
        // An empty account (nonce 0, balance 0, no code) that exists in state and appears in the BAL
        // only because it was read — not modified — must survive healing. The block did not touch it,
        // so processing it here and deleting it via the IsEmpty check drops a state trie leaf and
        // diverges from the target root. Regression for a wrong reassembled root when a BAL references
        // empty-but-present accounts as reads (e.g. touched precompiles / pre-EIP-158 empty accounts).
        SeedInitialState(Acc(TestItem.AddressA, 100), Acc(TestItem.AddressB, 0));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressA, 150), Acc(TestItem.AddressB, 0));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        ReadOnlyAccountChanges changed = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges(new BalanceChange(0, 150))
            .TestObject;
        ReadOnlyAccountChanges readOnly = Build.An.AccountChanges
            .WithAddress(TestItem.AddressB)
            .TestObject;
        BlockHeader lastPivot = SetupBlock(firstPivot, expected, Bal(changed, readOnly));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [], default);

        Assert.That(result, Is.True);
        _syncStore.Received(1).FinalizeSync(lastPivot);
    }

    [Test]
    public async Task Cleared_storage_slot_is_removed_from_flat_store()
    {
        // The BAL zeroes slot 1. The trie drops it, but the flat store must drop it too — otherwise
        // a stale zero entry survives and flat diverges from trie for eth_getStorageAt / re-sync.
        SeedInitialState(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05]), new Slot(2, [0x07])]));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressA, 100, slots: [new Slot(2, [0x07])]));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        ReadOnlyAccountChanges changes = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithStorageChanges(1, new StorageChange(0, UInt256.Zero))
            .TestObject;
        BlockHeader lastPivot = SetupBlock(firstPivot, expected, Bal(changes));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [StorageOf(TestItem.AddressA)], default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True);
            Assert.That(FlatSlotExists(TestItem.AddressA, 1), Is.False, "cleared slot must be removed from flat store");
            Assert.That(FlatSlotExists(TestItem.AddressA, 2), Is.True, "untouched slot must remain");
        }
    }

    [Test]
    public async Task Removed_account_storage_is_wiped_from_flat_store()
    {
        // Account A is emptied and dropped from the trie; its flat storage must be wiped too,
        // otherwise the removed account's slots linger in the flat store.
        SeedInitialState(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05])]), Acc(TestItem.AddressB, 200));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressB, 200));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(firstPivot, expected, BalanceBal(TestItem.AddressA, 0));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [StorageOf(TestItem.AddressA)], default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True);
            Assert.That(FlatSlotExists(TestItem.AddressA, 1), Is.False, "removed account's storage must be wiped from flat store");
        }
    }

    [Test]
    public async Task Removed_account_storage_written_in_the_same_bal_is_wiped_from_flat_store()
    {
        // Account A is emptied and writes slot 2 in the same BAL. The trie drops the account so the root
        // still matches, which makes any surviving flat slot a silent divergence: a later CREATE2 redeploy
        // at that address would be served the stale slots.
        SeedInitialState(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05])]), Acc(TestItem.AddressB, 200));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressB, 200));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        ReadOnlyAccountChanges changes = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges(new BalanceChange(0, 0))
            .WithStorageChanges(2, new StorageChange(0, (UInt256)7))
            .TestObject;
        BlockHeader lastPivot = SetupBlock(firstPivot, expected, Bal(changes));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [StorageOf(TestItem.AddressA)], default);

        Assert.That(result, Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(FlatSlotExists(TestItem.AddressA, 1), Is.False, "pre-existing slot must be wiped");
            Assert.That(FlatSlotExists(TestItem.AddressA, 2), Is.False, "slot written in the same batch must be wiped too");
        }
    }

    [Test]
    public void Storage_written_around_an_account_delete_is_wiped_from_flat_store([Values] bool blockByBlock)
    {
        SeedInitialState(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05])]), Acc(TestItem.AddressB, 200));
        Hash256 afterWrite = BuildRoot(
            Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05]), new Slot(3, [0x09]), new Slot(4, [0x0a])]),
            Acc(TestItem.AddressB, 200));
        Hash256 afterDelete = BuildRoot(Acc(TestItem.AddressB, 200));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader write = SetupBlock(firstPivot, afterWrite, Bal(Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithStorageChanges(3, new StorageChange(0, (UInt256)9))
            .WithStorageChanges(4, new StorageChange(0, (UInt256)10))
            .TestObject));
        BlockHeader delete = SetupBlock(write, afterDelete, BalanceBal(TestItem.AddressA, 0));
        BlockHeader writeAgain = SetupBlock(delete, afterDelete, Bal(Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithStorageChanges(4, new StorageChange(0, (UInt256)11))
            .WithStorageChanges(5, new StorageChange(0, (UInt256)12))
            .TestObject));

        Hash256? root = _healing.Reassemble([StorageOf(TestItem.AddressA)], default);
        if (blockByBlock)
        {
            root = _healing.ApplyRange(root!, firstPivot, write, default).Root;
            root = _healing.ApplyRange(root!, write, delete, default).Root;
            root = _healing.ApplyRange(root!, delete, writeAgain, default).Root;
        }
        else
        {
            root = _healing.ApplyRange(root!, firstPivot, writeAgain, default).Root;
        }

        UInt256[] watched = [1, 3, 4, 5];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(afterDelete), "apply must reach the target root");
            foreach (UInt256 slot in watched)
                Assert.That(FlatSlotExists(TestItem.AddressA, slot), Is.False, $"slot {slot} must be wiped from the flat store");
        }
    }

    [Test]
    public async Task Apply_returns_no_root_when_a_bal_is_missing()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(firstPivot, TestItem.KeccakB, bal: null);

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [], default);

        Assert.That(result, Is.False);
        _syncStore.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
    }

    [Test]
    public async Task Apply_returns_no_root_when_target_root_is_not_reached()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        // Target root does not match what applying the BAL produces.
        BlockHeader lastPivot = SetupBlock(firstPivot, TestItem.KeccakH, BalanceBal(TestItem.AddressA, 150));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [], default);

        Assert.That(result, Is.False);
        _syncStore.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
    }

    // Nothing persisted, so reassembly rebuilds an empty state trie and produces no root.
    [Test]
    public void Reassemble_returns_no_root_when_reassembly_fails() =>
        Assert.That(_healing.Reassemble([], default), Is.Null);

    [Test]
    public async Task Apply_propagates_a_bal_store_failure()
    {
        IBlockAccessListStore throwingStore = Substitute.For<IBlockAccessListStore>();
        throwingStore.Exists(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(true);
        throwingStore.Get(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(_ => throw new InvalidOperationException("boom"));
        FlatBalHealing healing = new(_blockTree, throwingStore, _reassembler, _persistence, _syncStore, _codeDb, new Lazy<ICodeRecovery>(_codeRecovery), LimboLogs.Instance);

        SeedInitialState(Acc(TestItem.AddressA, 100));
        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(firstPivot, TestItem.KeccakB, BalanceBal(TestItem.AddressA, 150));

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunOnce(healing, firstPivot, lastPivot, [], default));
        _syncStore.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
    }

    [Test]
    public async Task Apply_propagates_cancellation()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100));
        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(firstPivot, TestItem.KeccakB, BalanceBal(TestItem.AddressA, 150));

        await Assert.ThrowsAsync<OperationCanceledException>(() => RunOnce(_healing, firstPivot, lastPivot, [], new CancellationToken(canceled: true)));
        _syncStore.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
    }

    // A chunk's batch commits as it goes, so a cancellation must not land while the chunk is written: healing stops before
    // the chunk when cancelled while collecting its BALs, and after it when cancelled while writing it.
    [Test]
    public void A_cancellation_inside_a_chunk_stops_healing_on_a_chunk_boundary([Values] bool whileWriting)
    {
        (BlockHeader firstPivot, BlockHeader lastPivot, Hash256[] updatedStorages) = SetupThreeChunkHistory();
        using CancellationTokenSource cancellation = new();
        // The second chunk is the only one that writes code, and it does so for the first account it writes.
        _codeDb.WriteFunc = (_, _) =>
        {
            if (whileWriting) cancellation.Cancel();
            return true;
        };
        FlatBalHealing healing = CreateHealing(_persistence, _codeDb, number =>
        {
            if (!whileWriting && number == firstPivot.Number + FlatBalHealing.BalsChunkSize + 1) cancellation.Cancel();
        });

        Hash256 root = healing.Reassemble(updatedStorages, default)!;
        Assert.Throws<OperationCanceledException>(() => healing.ApplyRange(root, firstPivot, lastPivot, cancellation.Token));

        int chunksWritten = whileWriting ? 2 : 1;
        BlockHeader boundary = _blockTree.FindHeader(firstPivot.Number + (ulong)(chunksWritten * FlatBalHealing.BalsChunkSize))!;
        (SnapshotableMemColumnsDb<FlatDbColumns> stoppedThere, _) = HealUninterrupted(firstPivot, boundary, updatedStorages);
        using (stoppedThere)
        {
            AssertColumnsEqual(stoppedThere, Enum.GetValues<FlatDbColumns>());
        }
    }

    // The restarted sync reassembles the trie over the whole chunks a graceful stop left and replays every BAL from the
    // first pivot over them. BALs hold values rather than deltas, so it ends where a run that never stopped does.
    [Test]
    public void Replaying_from_the_first_pivot_after_a_stop_on_a_chunk_boundary_matches_an_uninterrupted_run([Values(1, 2)] int chunksWritten)
    {
        (BlockHeader firstPivot, BlockHeader lastPivot, Hash256[] updatedStorages) = SetupThreeChunkHistory();
        using CancellationTokenSource cancellation = new();
        FlatBalHealing stopped = CreateHealing(_persistence, _codeDb, number =>
        {
            if (number == firstPivot.Number + (ulong)(chunksWritten * FlatBalHealing.BalsChunkSize) + 1) cancellation.Cancel();
        });
        Hash256 stoppedRoot = stopped.Reassemble(updatedStorages, default)!;
        Assert.Throws<OperationCanceledException>(() => stopped.ApplyRange(stoppedRoot, firstPivot, lastPivot, cancellation.Token));

        Hash256? resumedRoot = _healing.ApplyRange(_healing.Reassemble(updatedStorages, default)!, firstPivot, lastPivot, default).Root;

        (SnapshotableMemColumnsDb<FlatDbColumns> uninterrupted, Hash256? uninterruptedRoot) = HealUninterrupted(firstPivot, lastPivot, updatedStorages);
        using (uninterrupted)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(uninterruptedRoot, Is.EqualTo(lastPivot.StateRoot), "uninterrupted root");
                Assert.That(resumedRoot, Is.EqualTo(lastPivot.StateRoot), "resumed root");
                Assert.That(_codeDb.Get(Keccak.Compute(DeployedCode).Bytes), Is.EqualTo(DeployedCode), "deployed code");
            }

            AssertColumnsEqual(uninterrupted, FlatDbColumns.Account, FlatDbColumns.Storage);
        }
    }

    // A stop during reassembly leaves the storage tries it rebuilt written, which the next one has to build on.
    [Test]
    public void Reassembling_again_after_a_stopped_reassembly_rebuilds_the_same_root()
    {
        AccountSpec[] state =
        [
            Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05]), new Slot(2, [0x06])]),
            Acc(TestItem.AddressB, 200),
            Acc(TestItem.AddressC, 300, slots: [new Slot(1, [0x07]), new Slot(2, [0x08])]),
        ];
        SeedInitialState(state);
        Hash256[] updatedStorages = [StorageOf(TestItem.AddressA), StorageOf(TestItem.AddressC)];
        // As snap leaves them: the storage roots of the accounts to heal and the state root are not persisted.
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            foreach (Hash256 address in updatedStorages) batch.SetStorageTrieNode(address, TreePath.Empty, default);
            batch.SetStateTrieNode(TreePath.Empty, default);
        }

        using CancellationTokenSource cancellation = new();
        Assert.Throws<OperationCanceledException>(() => _healing.Reassemble(new StopAfterStorageTries(updatedStorages, cancellation.Cancel), cancellation.Token));
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader(ReaderFlags.Sync))
        using (Assert.EnterMultipleScope())
        {
            foreach (Hash256 address in updatedStorages)
                Assert.That(reader.TryLoadStorageRlp(address, TreePath.Empty, ReadFlags.None), Is.Not.Null, "a storage trie rebuilt before the stop");
            Assert.That(reader.TryLoadStateRlp(TreePath.Empty, ReadFlags.None), Is.Null, "the state trie the stop came before");
        }

        Assert.That(_healing.Reassemble(updatedStorages, default), Is.EqualTo(BuildRoot(state)));
    }

    private static readonly byte[] DeployedCode = [0x60, 0x00, 0x60, 0x01];

    private static readonly AccountSpec[] ThreeChunkSeed =
    [
        Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05]), new Slot(2, [0x06])]),
        Acc(TestItem.AddressB, 200),
        Acc(TestItem.AddressC, 300, slots: [new Slot(1, [0x07])]),
        Acc(TestItem.AddressD, 400),
    ];

    /// <summary>
    /// Seeds <see cref="ThreeChunkSeed"/> and sets up three chunks of blocks whose BALs clear and set slots, empty
    /// accounts and revive them, and deploy code as the first write of the second chunk.
    /// </summary>
    /// <returns>The pivots healing goes between, the last at the root the BALs reach, and the accounts to heal.</returns>
    private (BlockHeader First, BlockHeader Last, Hash256[] UpdatedStorages) SetupThreeChunkHistory()
    {
        SeedInitialState(ThreeChunkSeed);
        Hash256 expected = BuildRoot(
            Acc(TestItem.AddressA, 150, slots: [new Slot(1, [0x04]), new Slot(2, [0x08])]),
            Acc(TestItem.AddressB, 250, nonce: 1, code: DeployedCode),
            Acc(TestItem.AddressC, 50),
            Acc(TestItem.AddressD, 10));

        const int firstChunkStart = 1;
        const int secondChunkStart = firstChunkStart + FlatBalHealing.BalsChunkSize;
        const int thirdChunkStart = secondChunkStart + FlatBalHealing.BalsChunkSize;
        const int lastBlock = thirdChunkStart + FlatBalHealing.BalsChunkSize - 1;

        BlockHeader firstPivot = Pivot(0, TestItem.KeccakA);
        BlockHeader lastPivot = firstPivot;
        for (int number = firstChunkStart; number <= lastBlock; number++)
        {
            ReadOnlyBlockAccessList bal = number switch
            {
                firstChunkStart + 2 => StorageBal(TestItem.AddressA, 1, 0),
                firstChunkStart + 9 => BalanceBal(TestItem.AddressC, 0),
                secondChunkStart => Bal(
                    Build.An.AccountChanges.WithAddress(TestItem.AddressB)
                        .WithNonceChanges(new NonceChange(0, 1)).WithCodeChanges(new CodeChange(0, DeployedCode)).TestObject,
                    Build.An.AccountChanges.WithAddress(TestItem.AddressD).WithBalanceChanges(new BalanceChange(1, 401)).TestObject,
                    Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithBalanceChanges(new BalanceChange(2, 150)).TestObject),
                secondChunkStart + 3 => BalanceBal(TestItem.AddressC, 50),
                secondChunkStart + 8 => BalanceBal(TestItem.AddressD, 0),
                thirdChunkStart => Bal(Build.An.AccountChanges.WithAddress(TestItem.AddressA)
                    .WithStorageChanges(1, new StorageChange(0, (UInt256)4))
                    .WithStorageChanges(2, new StorageChange(0, (UInt256)8)).TestObject),
                thirdChunkStart + 7 => Bal(
                    Build.An.AccountChanges.WithAddress(TestItem.AddressD).WithBalanceChanges(new BalanceChange(0, 10)).TestObject,
                    Build.An.AccountChanges.WithAddress(TestItem.AddressB).WithBalanceChanges(new BalanceChange(1, 250)).TestObject),
                _ => EmptyBal()
            };
            lastPivot = SetupBlock(lastPivot, number == lastBlock ? expected : TestItem.KeccakA, bal);
        }

        return (firstPivot, lastPivot, [StorageOf(TestItem.AddressA), StorageOf(TestItem.AddressC)]);
    }

    /// <summary>Heals a second database seeded with <see cref="ThreeChunkSeed"/> without interruption.</summary>
    /// <returns>The database, to dispose, and the root healing reached, or <c>null</c> where it is not <paramref name="to"/>'s.</returns>
    private (SnapshotableMemColumnsDb<FlatDbColumns> Db, Hash256? Root) HealUninterrupted(BlockHeader from, BlockHeader to, IReadOnlyCollection<Hash256> updatedStorages)
    {
        SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        RocksDbPersistence persistence = new(db, LimboLogs.Instance, NullTrieNodeLog.Instance);
        CommitState(persistence, ThreeChunkSeed);
        FlatBalHealing healing = CreateHealing(persistence, new MemDb());
        return (db, healing.ApplyRange(healing.Reassemble(updatedStorages, default)!, from, to, default).Root);
    }

    /// <param name="beforeBal">Called with the number of each block before healing reads its BAL to apply it.</param>
    private FlatBalHealing CreateHealing(IPersistence persistence, IDb codeDb, Action<ulong>? beforeBal = null)
    {
        IBlockAccessListStore balStore = _balStore;
        if (beforeBal is not null)
        {
            balStore = Substitute.For<IBlockAccessListStore>();
            balStore.Exists(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(call => _balStore.Exists(call.ArgAt<ulong>(0), call.ArgAt<Hash256>(1)));
            balStore.Get(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(call =>
            {
                beforeBal(call.ArgAt<ulong>(0));
                return _balStore.Get(call.ArgAt<ulong>(0), call.ArgAt<Hash256>(1));
            });
        }

        return new(_blockTree, balStore, new TrieReassembler(persistence, LimboLogs.Instance), persistence, _syncStore, codeDb, new Lazy<ICodeRecovery>(_codeRecovery), LimboLogs.Instance);
    }

    private void AssertColumnsEqual(IColumnsDb<FlatDbColumns> expected, params FlatDbColumns[] columns)
    {
        using (Assert.EnterMultipleScope())
        {
            foreach (FlatDbColumns column in columns)
                Assert.That(ReadColumn(_columnsDb, column), Is.EqualTo(ReadColumn(expected, column)), $"{column} column");
        }

        static SortedDictionary<string, string> ReadColumn(IColumnsDb<FlatDbColumns> db, FlatDbColumns column) =>
            new(db.GetColumnDb(column).GetAll().ToDictionary(static kv => Convert.ToHexString(kv.Key), static kv => Convert.ToHexString(kv.Value)));
    }

    /// <summary>Calls <paramref name="stop"/> once trie reassembly has gone through the accounts to heal, rebuilding their storage tries, and before it rebuilds the state trie.</summary>
    private sealed class StopAfterStorageTries(IReadOnlyCollection<Hash256> accounts, Action stop) : IReadOnlyCollection<Hash256>
    {
        public int Count => accounts.Count;

        public IEnumerator<Hash256> GetEnumerator()
        {
            foreach (Hash256 account in accounts) yield return account;
            stop();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private readonly record struct Slot(UInt256 Key, byte[] Value);

    private sealed record AccountSpec(Address Address, UInt256 Balance, ulong Nonce, byte[]? Code, Slot[] Slots);

    private static AccountSpec Acc(Address address, UInt256 balance, ulong nonce = 0, byte[]? code = null, Slot[]? slots = null) =>
        new(address, balance, nonce, code, slots ?? []);

    private static Hash256 StorageOf(Address address) => address.ToAccountPath.ToCommitment();

    private bool FlatSlotExists(Address address, UInt256 slot)
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader(ReaderFlags.Sync);
        UInt256 value = default;
        return reader.TryGetSlot(address, slot, ref value);
    }

    // Composes the split IBalHealing primitives into a single-round heal (reassemble → apply → recover code → finalize) so these
    // tests stay focused on the flat apply logic. Failures propagate as they do in production; the multi-round
    // orchestration in StateSyncRunner.RunBalHealing is not covered here.
    private static Task<bool> RunOnce(FlatBalHealing healing, BlockHeader firstPivot, BlockHeader lastPivot, IReadOnlyCollection<Hash256> updatedStorages, CancellationToken token)
    {
        Hash256? root = healing.Reassemble(updatedStorages, token);
        if (root is null) return Task.FromResult(false);

        root = healing.ApplyRange(root, firstPivot, lastPivot, token).Root;
        if (root is null) return Task.FromResult(false);

        return FinalizeOnceCodeRecovered();

        async Task<bool> FinalizeOnceCodeRecovered()
        {
            if (!await healing.TryRecoverMissingCode(token)) return false;
            healing.FinalizeSync(lastPivot);
            return true;
        }
    }

    private static BlockHeader Pivot(ulong number, Hash256 stateRoot) =>
        Build.A.BlockHeader.WithNumber(number).WithStateRoot(stateRoot).TestObject;

    private BlockHeader SetupBlock(BlockHeader parent, Hash256 stateRoot, ReadOnlyBlockAccessList? bal)
    {
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(parent.Number + 1)
            .WithParentHash(parent.Hash!)
            .WithStateRoot(stateRoot)
            .TestObject;
        _blockTree.FindHeader(header.Number).Returns(header);
        if (bal is not null)
            _balStore.Insert(header.Number, header.Hash!, bal);
        return header;
    }

    private void SeedInitialState(params AccountSpec[] accounts) => CommitState(_persistence, accounts);

    private static Hash256 BuildRoot(params AccountSpec[] accounts)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        RocksDbPersistence persistence = new(db, LimboLogs.Instance, NullTrieNodeLog.Instance);
        return CommitState(persistence, accounts);
    }

    // Writes a consistent flat state (flat leaves + trie nodes) and returns the state root. Code bytes are not
    // stored - the code db is written by healing alone.
    private static Hash256 CommitState(RocksDbPersistence persistence, AccountSpec[] accounts)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        StateTree stateTree = new(new PersistenceTrieStoreAdapter(reader, batch, enableDoubleWriteCheck: false), LimboLogs.Instance);

        foreach (AccountSpec spec in accounts)
        {
            Account account = new(spec.Nonce, spec.Balance, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString);

            if (spec.Code is { } code)
                account = account.WithChangedCodeHash(Keccak.Compute(code));

            if (spec.Slots is { Length: > 0 } slots)
            {
                StorageTree storage = new(
                    new PersistenceStorageTrieStoreAdapter(reader, batch, spec.Address.ToAccountPath.ToCommitment(), enableDoubleWriteCheck: false),
                    LimboLogs.Instance);
                foreach (Slot slot in slots)
                {
                    storage.Set(slot.Key, slot.Value);
                    batch.SetStorage(spec.Address, slot.Key, BaseFlatPersistence.DecodeSlotValue(slot.Value));
                }
                storage.Commit();
                account = account.WithChangedStorageRoot(storage.RootHash);
            }

            stateTree.Set(spec.Address, account);
            batch.SetAccount(spec.Address, account);
        }

        stateTree.Commit();
        return stateTree.RootHash;
    }

    private static ReadOnlyBlockAccessList EmptyBal() => Build.A.BlockAccessList.TestObject;

    private static ReadOnlyBlockAccessList Bal(params ReadOnlyAccountChanges[] changes) =>
        Build.A.BlockAccessList.WithAccountChanges(changes).TestObject;

    private static ReadOnlyBlockAccessList BalanceBal(Address address, UInt256 balance) =>
        Bal(Build.An.AccountChanges.WithAddress(address).WithBalanceChanges(new BalanceChange(0, balance)).TestObject);

    private static ReadOnlyBlockAccessList StorageBal(Address address, UInt256 slot, UInt256 value) =>
        Bal(Build.An.AccountChanges.WithAddress(address).WithStorageChanges(slot, new StorageChange(0, value)).TestObject);
}
