// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Tracing;

public class BlockAccessListPrefixStateSeedSourceTests
{
    private const int TransactionCount = 4;
    private static readonly Address Contract = TestItem.AddressA;
    private static readonly Address SystemContract = TestItem.AddressB;
    private static readonly Address Created = TestItem.AddressC;
    private static readonly Address Drained = TestItem.AddressD;
    private static readonly Address Unchanged = TestItem.AddressE;
    private static readonly byte[] CreatedCode = [0x60, 0x00, 0x00];

    // Block access indices: 0 is the system calls, i + 1 is transaction i, TransactionCount + 1 follows the transactions.
    private static ReadOnlyBlockAccessList BuildAccessList(Hash256 wireHash = null) => new(
    [
        new ReadOnlyAccountChanges(Contract,
            [new ReadOnlySlotChanges(UInt256.One, [new StorageChange(1, 10), new StorageChange(3, 30), new StorageChange(TransactionCount + 1, 50)])],
            [new UInt256(9)],
            [new BalanceChange(2, 200)], [], []),
        new ReadOnlyAccountChanges(SystemContract,
            [new ReadOnlySlotChanges(UInt256.One, [new StorageChange(0, 7)])], [], [new BalanceChange(0, 1)], [], []),
        new ReadOnlyAccountChanges(Created,
            [new ReadOnlySlotChanges(UInt256.Zero, [new StorageChange(2, 5)])], [],
            [new BalanceChange(2, 3)], [new NonceChange(2, 1)], [new CodeChange(2, CreatedCode)]),
        new ReadOnlyAccountChanges(Drained, [], [], [new BalanceChange(3, 0)], [], []),
        new ReadOnlyAccountChanges(Unchanged, [], [UInt256.One], [], [], []),
    ], 12, wireHash);

    private static BlockAccessListReadOverlay OverlayBefore(int transactionIndex) =>
        new(new BlockAccessListPrefix(TestItem.KeccakA, BuildAccessList(), TransactionCount)) { TransactionIndex = (uint)transactionIndex };

    [TestCase(0, false, 0, TestName = "BeforeTheFirstTransaction_NothingIsSeeded")]
    [TestCase(1, true, 10, TestName = "AfterTheFirstTransaction_ItsWriteIsSeen")]
    [TestCase(2, true, 10, TestName = "BeforeATransactionThatWrites_TheEarlierWriteIsSeen")]
    [TestCase(3, true, 30, TestName = "AfterTheSecondWrite_TheLatestIsSeen")]
    [TestCase(4, true, 30, TestName = "AfterTheLastTransaction_ThePostExecutionWriteIsLeftToItsSystemCall")]
    public void TryGetStorage_ReadsTheLatestTransactionWriteBeforeTheTarget(int transactionIndex, bool expectedKnown, int expectedValue)
    {
        BlockAccessListReadOverlay overlay = OverlayBefore(transactionIndex);

        bool known = overlay.TryGetStorage(Contract, UInt256.One, out UInt256 value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(known, Is.EqualTo(expectedKnown), "a slot is answered only once a transaction before the target wrote it");
            if (known) Assert.That(value, Is.EqualTo((UInt256)expectedValue), "the latest write below the target wins");
            Assert.That(overlay.HasStorage(Contract), Is.EqualTo(expectedKnown), "storage is reported exactly when a transaction before the target wrote a slot");
        }
    }

    [Test]
    public void SystemCallWrites_AreLeftToTheSystemCallThatReplaysThem()
    {
        BlockAccessListReadOverlay overlay = OverlayBefore(TransactionCount);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(overlay.TryGetStorage(SystemContract, UInt256.One, out _), Is.False, "index 0 belongs to the system calls, which run again on every trace");
            Assert.That(overlay.HasStorage(SystemContract), Is.False, "a system call write must not make the overlay claim storage, or it would be refused");
            Assert.That(overlay.TryGetAccount(SystemContract, new Account(1), out _), Is.False, "the system call's balance change is answered by the system call itself");
        }
    }

    [Test]
    public void TryGetAccount_WhenATransactionCreatedTheAccount_ReturnsItsFieldsAndAStorageRootThatShowsItsSlots()
    {
        BlockAccessListReadOverlay overlay = OverlayBefore(2);

        bool known = overlay.TryGetAccount(Created, null, out Account account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(known, Is.True, "a transaction before the target created the account");
            Assert.That(account.Nonce, Is.EqualTo(1ul));
            Assert.That(account.Balance, Is.EqualTo((UInt256)3));
            Assert.That(account.CodeHash, Is.EqualTo(Keccak.Compute(CreatedCode)), "the code hash comes from the code the list carries");
            Assert.That(account.StorageRoot, Is.EqualTo(IStateReadOverlay.NonEmptyStorageRoot), "an empty root would have the storage provider skip the slots written since");
        }
    }

    [Test]
    public void TryGetAccount_BeforeTheTransactionThatCreatesIt_LeavesTheUnderlyingAccount()
    {
        BlockAccessListReadOverlay overlay = OverlayBefore(1);

        Assert.That(overlay.TryGetAccount(Created, null, out _), Is.False, "a change made by the target itself is not part of its prestate");
    }

    [Test]
    public void TryGetAccount_WhenATransactionLeftTheAccountEmpty_ReportsItDeleted()
    {
        BlockAccessListReadOverlay overlay = OverlayBefore(TransactionCount);

        bool known = overlay.TryGetAccount(Drained, new Account(5), out Account account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(known, Is.True, "the balance change is the prefix's");
            Assert.That(account, Is.Null, "EIP-161 deletes an account a transaction touched and left empty");
        }
    }

    [Test]
    public void TryGetAccount_WhenOnlyTheBalanceChanged_KeepsTheOtherFieldsOfTheUnderlyingAccount()
    {
        BlockAccessListReadOverlay overlay = OverlayBefore(2);
        Account underlying = new(7, 100, TestItem.KeccakB, TestItem.KeccakC);

        bool known = overlay.TryGetAccount(Contract, underlying, out Account account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(known, Is.True, "transaction 1, at block access index 2, changed the balance");
            Assert.That(account.Balance, Is.EqualTo((UInt256)200), "the balance transaction 1 left");
            Assert.That(account.Nonce, Is.EqualTo(underlying.Nonce), "a field no transaction changed stays the parent's");
            Assert.That(account.CodeHash, Is.EqualTo(underlying.CodeHash), "a field no transaction changed stays the parent's");
            Assert.That(account.StorageRoot, Is.EqualTo(underlying.StorageRoot), "a real root is kept: only an empty one would hide the written slot");
        }
    }

    [TestCase(false, true, TestName = "WhenOnlyStorageChangedOverAnEmptyRoot_ReportsANonEmptyRoot")]
    [TestCase(true, false, TestName = "WhenOnlyStorageChangedOverARealRoot_LeavesTheUnderlyingAccount")]
    public void TryGetAccount_WhenOnlyStorageChanged_TouchesNothingButAnEmptyRoot(bool underlyingHasStorage, bool expectedKnown)
    {
        BlockAccessListReadOverlay overlay = OverlayBefore(1);
        Account underlying = underlyingHasStorage ? new Account(7, 100, TestItem.KeccakB, TestItem.KeccakC) : new Account(7, 100);

        bool known = overlay.TryGetAccount(Contract, underlying, out Account account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(known, Is.EqualTo(expectedKnown), "the storage provider skips an account whose root is empty, so only that root is replaced");
            if (known) Assert.That(account.StorageRoot, Is.EqualTo(IStateReadOverlay.NonEmptyStorageRoot), "the slot transaction 0 wrote must stay readable");
            if (known) Assert.That(account.Balance, Is.EqualTo(underlying.Balance), "the balance change belongs to a later transaction");
        }
    }

    [Test]
    public void ReadsThatTheListAnswersOrLeavesToTheParent_AllocateNothing()
    {
        BlockAccessListReadOverlay overlay = OverlayBefore(TransactionCount);
        Account underlying = new(1);
        ReadAll(overlay, underlying);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++) ReadAll(overlay, underlying);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.Zero, "slot reads, storage probes and reads of accounts the prefix left alone must not allocate");

        static void ReadAll(BlockAccessListReadOverlay overlay, Account underlying)
        {
            overlay.TryGetStorage(Contract, UInt256.One, out _);
            overlay.TryGetStorage(Contract, new UInt256(9), out _);
            overlay.TryGetStorage(Unchanged, UInt256.One, out _);
            overlay.HasStorage(Contract);
            overlay.TryGetAccount(Unchanged, underlying, out _);
            overlay.TryGetAccount(SystemContract, underlying, out _);
        }
    }

    [TestCase(false, true, true, TestName = "WhenTheCarriedListMatchesTheHeader_SeedsWithoutTheStore")]
    [TestCase(true, true, true, TestName = "WhenOnlyTheStoreHoldsTheList_SeedsFromTheStore")]
    [TestCase(true, false, false, TestName = "WhenTheStoredListDoesNotMatchTheHeader_Refuses")]
    [TestCase(false, false, false, TestName = "WhenTheListIsMissing_Refuses")]
    public void TrySeed_TakesOnlyAListThatHashesToTheHeader(bool stored, bool matching, bool expected)
    {
        IBlockAccessListStore store = Substitute.For<IBlockAccessListStore>();
        Hash256 wireHash = matching ? TestItem.KeccakA : TestItem.KeccakB;
        Block block = Build.A.Block.WithNumber(3).WithBlockAccessListHash(TestItem.KeccakA)
            .WithBlockAccessList(stored ? null : BuildAccessList(matching ? wireHash : null)).TestObject;
        store.Get(3ul, block.Hash).Returns(stored ? BuildAccessList(wireHash) : null);
        BlockAccessListPrefixStateSeedSource seeds = new(NullPrefixStateSeedSource.Instance, store, new TestSpecProvider(Amsterdam.Instance));
        StateReadOverlaySlot slot = new();

        bool seeded = seeds.TrySeed(block, 0, slot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seeded, Is.EqualTo(expected), "only a list that hashes to the header's commitment is trusted");
            Assert.That(slot.Current is not null, Is.EqualTo(expected), "a refused seed leaves the slot disarmed");
            Assert.That(seeds.TryOpenBlock(block, out ICoveredBlock covered), Is.EqualTo(expected), "a whole-block trace follows the same rule");
            covered?.Dispose();
        }
        slot.Disarm();
    }

    [Test]
    public void TrySeed_OnABlockWithoutAnAccessList_IsLeftToTheSourceUnderneath()
    {
        IPrefixStateSeedSource inner = Substitute.For<IPrefixStateSeedSource>();
        Block block = Build.A.Block.WithNumber(3).TestObject;
        StateReadOverlaySlot slot = new();
        inner.TrySeed(block, 2, slot).Returns(true);
        BlockAccessListPrefixStateSeedSource seeds = new(inner, Substitute.For<IBlockAccessListStore>(), new TestSpecProvider(Prague.Instance));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seeds.TrySeed(block, 2, slot), Is.True, "a block before the access list fork is seeded by whatever sits underneath");
            Assert.That(seeds.Enabled, Is.False, "a chain that never carries access lists enables nothing the source underneath does not");
        }
    }

    [Test]
    public void TrySeed_OnABlockWithAnAccessList_NeverAsksTheSourceUnderneath()
    {
        IPrefixStateSeedSource inner = Substitute.For<IPrefixStateSeedSource>();
        inner.TrySeed(Arg.Any<Block>(), Arg.Any<int>(), Arg.Any<StateReadOverlaySlot>()).Returns(true);
        Block block = Build.A.Block.WithNumber(3).WithBlockAccessListHash(TestItem.KeccakA).TestObject;
        BlockAccessListPrefixStateSeedSource seeds = new(inner, Substitute.For<IBlockAccessListStore>(), new TestSpecProvider(Amsterdam.Instance));

        bool seeded = seeds.TrySeed(block, 1, new StateReadOverlaySlot());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seeded, Is.False, "without its access list the block is replayed, as before");
            inner.DidNotReceiveWithAnyArgs().TrySeed(default, default, default);
        }
    }

    [Test]
    public void WorkerSeeds_RePointOneOverlayAtEachTransaction()
    {
        IBlockAccessListStore store = Substitute.For<IBlockAccessListStore>();
        Block block = Build.A.Block.WithNumber(3).WithTransactions(TransactionCount, Prague.Instance)
            .WithBlockAccessListHash(TestItem.KeccakA).WithBlockAccessList(BuildAccessList(TestItem.KeccakA)).TestObject;
        BlockAccessListPrefixStateSeedSource seeds = new(NullPrefixStateSeedSource.Instance, store, new TestSpecProvider(Amsterdam.Instance));
        Assert.That(seeds.TryOpenBlock(block, out ICoveredBlock covered), Is.True, "precondition: the block's list is valid");
        using ICoveredBlock coverage = covered;
        IPrefixStateSeedSource worker = coverage.CreateWorkerSeeds();
        StateReadOverlaySlot slot = new();

        Assert.That(worker.TrySeed(block, 1, slot), Is.True);
        IStateReadOverlay first = slot.Current;
        bool firstKnown = first.TryGetStorage(Contract, UInt256.One, out UInt256 firstValue);
        Assert.That(worker.TrySeed(block, 3, slot), Is.True);
        bool laterKnown = slot.Current.TryGetStorage(Contract, UInt256.One, out UInt256 laterValue);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(worker.SeedsFromBlockAccessLists, Is.True, "a worker's seeds come from the list, so block processing accepts them");
            Assert.That(slot.Current, Is.SameAs(first), "one overlay per worker, re-pointed rather than allocated per transaction");
            Assert.That(firstKnown && firstValue == 10, Is.True, "before transaction 1 the slot holds what transaction 0 wrote");
            Assert.That(laterKnown && laterValue == 30, Is.True, "before transaction 3 the slot holds what transaction 2 wrote");
            Assert.That(worker.TrySeed(Build.A.Block.WithNumber(4).TestObject, 1, new StateReadOverlaySlot()), Is.False, "a worker seeds only the block it was opened for");
        }
        slot.Disarm();
    }
}
