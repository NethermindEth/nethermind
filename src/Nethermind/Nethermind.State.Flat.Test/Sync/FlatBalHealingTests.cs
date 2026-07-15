// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Sync;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.Synchronization.FastSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync;

[TestFixture]
public class FlatBalHealingTests
{
    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private RocksDbPersistence _persistence = null!;
    private TrieReassembler _reassembler = null!;
    private MemDb _codeDb = null!;
    private MemDb _balDb = null!;
    private BlockAccessListStore _balStore = null!;
    private IBlockTree _blockTree = null!;
    private ITreeSyncStore _syncStore = null!;
    private FlatBalHealing _healing = null!;

    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _persistence = new RocksDbPersistence(_columnsDb, LimboLogs.Instance);
        _reassembler = new TrieReassembler(_persistence, LimboLogs.Instance);
        _codeDb = new MemDb();
        _balDb = new MemDb();
        _balStore = new BlockAccessListStore(_balDb);
        _blockTree = Substitute.For<IBlockTree>();
        _syncStore = Substitute.For<ITreeSyncStore>();
        _healing = new(_blockTree, _balStore, _reassembler, _syncStore, _persistence, _codeDb, LimboLogs.Instance);
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
        BlockHeader lastPivot = SetupBlock(11, expected, BalanceBal(TestItem.AddressA, 150));

        bool result = await _healing.Run(firstPivot, lastPivot, [], default);

        Assert.That(result, Is.True);
        _syncStore.Received(1).FinalizeSync(lastPivot);
    }

    [Test]
    public async Task Heals_across_multiple_chunks()
    {
        // 260 blocks crosses the 256-block chunk boundary: chunk 2 must see chunk 1's committed state.
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
            lastPivot = SetupBlock(number, number == 260 ? expected : TestItem.KeccakA, bal);
        }

        bool result = await _healing.Run(firstPivot, lastPivot, [], default);

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
        BlockHeader lastPivot = SetupBlock(11, expected, Bal(changes));

        bool result = await _healing.Run(firstPivot, lastPivot, [], default);

        Assert.That(result, Is.True);
        Assert.That(_codeDb.Get(codeHash.Bytes), Is.EqualTo(code));
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
        BlockHeader lastPivot = SetupBlock(11, expected, Bal(changes));

        bool result = await _healing.Run(firstPivot, lastPivot, [StorageOf(TestItem.AddressA)], default);

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
        BlockHeader lastPivot = SetupBlock(11, expected, Bal(changes));

        bool result = await _healing.Run(firstPivot, lastPivot, [StorageOf(TestItem.AddressA)], default);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task Heals_when_bal_empties_account()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100), Acc(TestItem.AddressB, 200));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressB, 200));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(11, expected, BalanceBal(TestItem.AddressA, 0));

        bool result = await _healing.Run(firstPivot, lastPivot, [], default);

        Assert.That(result, Is.True);
        _syncStore.Received(1).FinalizeSync(lastPivot);
    }

    [Test]
    public async Task Emptied_account_that_still_has_storage_is_removed_from_state()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05])]), Acc(TestItem.AddressB, 200));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressB, 200));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(11, expected, BalanceBal(TestItem.AddressA, 0));

        bool result = await _healing.Run(firstPivot, lastPivot, [StorageOf(TestItem.AddressA)], default);

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
        BlockHeader lastPivot = SetupBlock(11, expected, Bal(changes));

        bool result = await _healing.Run(firstPivot, lastPivot, [StorageOf(TestItem.AddressA)], default);

        Assert.That(result, Is.True);
        Assert.That(FlatSlotExists(TestItem.AddressA, 1), Is.False, "cleared slot must be removed from flat store");
        Assert.That(FlatSlotExists(TestItem.AddressA, 2), Is.True, "untouched slot must remain");
    }

    [Test]
    public async Task Removed_account_storage_is_wiped_from_flat_store()
    {
        // Account A is emptied and dropped from the trie; its flat storage must be wiped too,
        // otherwise the removed account's slots linger in the flat store.
        SeedInitialState(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05])]), Acc(TestItem.AddressB, 200));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressB, 200));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(11, expected, BalanceBal(TestItem.AddressA, 0));

        bool result = await _healing.Run(firstPivot, lastPivot, [StorageOf(TestItem.AddressA)], default);

        Assert.That(result, Is.True);
        Assert.That(FlatSlotExists(TestItem.AddressA, 1), Is.False, "removed account's storage must be wiped from flat store");
    }

    [Test]
    public async Task Falls_back_when_a_bal_is_missing()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(11, TestItem.KeccakB, bal: null);

        bool result = await _healing.Run(firstPivot, lastPivot, [], default);

        Assert.That(result, Is.False);
        _syncStore.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
    }

    [Test]
    public async Task Falls_back_when_apply_does_not_reach_target_root()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        // Target root does not match what applying the BAL produces.
        BlockHeader lastPivot = SetupBlock(11, TestItem.KeccakH, BalanceBal(TestItem.AddressA, 150));

        bool result = await _healing.Run(firstPivot, lastPivot, [], default);

        Assert.That(result, Is.False);
        _syncStore.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
    }

    [Test]
    public async Task Falls_back_when_reassembly_produces_no_root()
    {
        ITrieReassembler reassembler = Substitute.For<ITrieReassembler>();
        reassembler.TryReassemble(Arg.Any<IReadOnlyCollection<Hash256>>()).Returns((Hash256?)null);
        FlatBalHealing healing = new(_blockTree, _balStore, reassembler, _syncStore, _persistence, _codeDb, LimboLogs.Instance);

        SeedInitialState(Acc(TestItem.AddressA, 100));
        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(11, TestItem.KeccakB, BalanceBal(TestItem.AddressA, 150));

        bool result = await healing.Run(firstPivot, lastPivot, [], default);

        Assert.That(result, Is.False);
        _syncStore.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
    }

    [Test]
    public async Task Falls_back_when_apply_throws()
    {
        IBlockAccessListStore throwingStore = Substitute.For<IBlockAccessListStore>();
        throwingStore.Exists(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(true);
        throwingStore.Get(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(_ => throw new InvalidOperationException("boom"));
        FlatBalHealing healing = new(_blockTree, throwingStore, _reassembler, _syncStore, _persistence, _codeDb, LimboLogs.Instance);

        SeedInitialState(Acc(TestItem.AddressA, 100));
        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(11, TestItem.KeccakB, BalanceBal(TestItem.AddressA, 150));

        bool result = await healing.Run(firstPivot, lastPivot, [], default);

        Assert.That(result, Is.False);
        _syncStore.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
    }

    [Test]
    public async Task Falls_back_when_cancelled()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100));
        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(11, TestItem.KeccakB, BalanceBal(TestItem.AddressA, 150));

        bool result = await _healing.Run(firstPivot, lastPivot, [], new CancellationToken(canceled: true));

        Assert.That(result, Is.False);
        _syncStore.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
    }

    private readonly record struct Slot(UInt256 Key, byte[] Value);

    private sealed record AccountSpec(Address Address, UInt256 Balance, ulong Nonce, byte[]? Code, Slot[] Slots);

    private static AccountSpec Acc(Address address, UInt256 balance, ulong nonce = 0, byte[]? code = null, Slot[]? slots = null) =>
        new(address, balance, nonce, code, slots ?? []);

    private static Hash256 StorageOf(Address address) => address.ToAccountPath.ToCommitment();

    private bool FlatSlotExists(Address address, UInt256 slot)
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader(ReaderFlags.Sync);
        SlotValue value = default;
        return reader.TryGetSlot(address, slot, ref value);
    }

    private static BlockHeader Pivot(ulong number, Hash256 stateRoot) =>
        Build.A.BlockHeader.WithNumber(number).WithStateRoot(stateRoot).TestObject;

    private BlockHeader SetupBlock(ulong number, Hash256 stateRoot, ReadOnlyBlockAccessList? bal)
    {
        BlockHeader header = Pivot(number, stateRoot);
        // FlatBalHealing calls the single-arg FindHeader(ulong); stub that exact overload.
        _blockTree.FindHeader(number).Returns(header);
        if (bal is not null)
            _balStore.Insert(number, header.Hash!, bal);
        return header;
    }

    private void SeedInitialState(params AccountSpec[] accounts) => CommitState(_persistence, accounts);

    private Hash256 BuildRoot(params AccountSpec[] accounts)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        RocksDbPersistence persistence = new(db, LimboLogs.Instance);
        return CommitState(persistence, accounts);
    }

    // Writes a consistent flat state (flat leaves + trie nodes) and returns the state root.
    private Hash256 CommitState(RocksDbPersistence persistence, AccountSpec[] accounts)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        StateTree stateTree = new(new PersistenceTrieStoreAdapter(reader, batch, enableDoubleWriteCheck: false), LimboLogs.Instance);

        foreach (AccountSpec spec in accounts)
        {
            Account account = new(spec.Nonce, spec.Balance, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString);

            if (spec.Code is { } code)
            {
                Hash256 codeHash = Keccak.Compute(code);
                _codeDb.Set(codeHash.Bytes, code);
                account = account.WithChangedCodeHash(codeHash);
            }

            if (spec.Slots is { Length: > 0 } slots)
            {
                StorageTree storage = new(
                    new PersistenceStorageTrieStoreAdapter(reader, batch, spec.Address.ToAccountPath.ToCommitment(), enableDoubleWriteCheck: false),
                    LimboLogs.Instance);
                foreach (Slot slot in slots)
                {
                    storage.Set(slot.Key, slot.Value);
                    batch.SetStorage(spec.Address, slot.Key, SlotValue.FromSpanWithoutLeadingZero(slot.Value));
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
}
