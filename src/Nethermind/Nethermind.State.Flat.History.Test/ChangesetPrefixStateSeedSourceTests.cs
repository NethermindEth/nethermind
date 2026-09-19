// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class ChangesetPrefixStateSeedSourceTests
{
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _columns = null!;
    private TransactionChangesetIndex _index = null!;
    private Block _block = null!;

    [SetUp]
    public void SetUp()
    {
        _columns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _index = new TransactionChangesetIndex(_columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        _block = Build.A.Block.WithNumber(7).WithTransactions(Build.A.Transaction.WithNonce(0).TestObject, Build.A.Transaction.WithNonce(1).TestObject).TestObject;
        using TransactionChangesetIndex.BlockCapture capture = _index.StartBlock(7);
        capture.Tracer.StartNewBlockTrace(_block);
        ITxTracer first = capture.Tracer.StartNewTxTrace(_block.Transactions[0]);
        first.ReportBalanceChange(TestItem.AddressA, 100, 42);
        capture.Tracer.EndTxTrace();
        ITxTracer second = capture.Tracer.StartNewTxTrace(_block.Transactions[1]);
        second.ReportNonceChange(TestItem.AddressB, 0, 1);
        capture.Tracer.EndTxTrace();
        capture.Tracer.EndBlockTrace();
        capture.Commit();
        _index.TryClaim(7, 7);
    }

    [TearDown]
    public void TearDown() => _columns.Dispose();

    [Test]
    public void SingleTransactionOnlyProvider_DefaultsToWholeBlockRefusal()
    {
        IPrefixStateSeedSource legacy = new SingleTransactionOnlyProvider();
        bool opened = legacy.TryOpenBlock(_block, out ICoveredBlock? covered);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(opened, Is.False);
            Assert.That(covered, Is.Null);
            Assert.That(legacy.TrySeed(_block, 1, new StateReadOverlaySlot()), Is.True);
        }
    }

    private sealed class SingleTransactionOnlyProvider : IPrefixStateSeedSource
    {
        public bool Enabled => true;
        public bool TrySeed(Block block, int transactionIndex, StateReadOverlaySlot slot) => true;
    }

    [Test]
    public void CoveredBlocks_KeepReadCachesExclusiveAndClearThemBeforeReuse()
    {
        ChangesetPrefixStateSeedSource source = new(_index);
        Assert.That(source.TryOpenBlock(_block, out ICoveredBlock? first), Is.True);
        Assert.That(source.TryOpenBlock(_block, out ICoveredBlock? second), Is.True);
        using ICoveredBlock secondLease = second!;
        StateReadOverlaySlot firstSlot = new();
        StateReadOverlaySlot secondSlot = new();
        Assert.That(first!.CreateWorkerSeeds().TrySeed(_block, 1, firstSlot), Is.True);
        Assert.That(second!.CreateWorkerSeeds().TrySeed(_block, 1, secondSlot), Is.True);
        BlockReadCache firstCache = firstSlot.Cache!;
        firstCache.SetAccount(TestItem.AddressC, new Account(1, 42));
        firstCache.SetSlot(TestItem.AddressC, UInt256.One, (UInt256)42);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(secondSlot.Cache, Is.Not.SameAs(firstCache));
            Assert.That(secondSlot.Cache!.TryGetAccount(TestItem.AddressC, out _), Is.False);
        }
        firstSlot.Disarm();
        first.Dispose();
        first.Dispose();

        Assert.That(source.TryOpenBlock(_block, out ICoveredBlock? reopened), Is.True);
        using ICoveredBlock reopenedLease = reopened!;
        StateReadOverlaySlot reopenedSlot = new();
        Assert.That(reopened!.CreateWorkerSeeds().TrySeed(_block, 1, reopenedSlot), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reopenedSlot.Cache!.TryGetAccount(TestItem.AddressC, out _), Is.False);
            Assert.That(reopenedSlot.Cache.TryGetSlot(TestItem.AddressC, UInt256.One, out _), Is.False);
            Assert.That(reopenedSlot.Cache, Is.Not.SameAs(secondSlot.Cache));
        }
        secondSlot.Disarm();
        reopenedSlot.Disarm();
    }

    [Test]
    public void ACoveredPrefix_ArmsTheSlotWithItsOverlay()
    {
        StateReadOverlaySlot slot = new();
        ChangesetPrefixStateSeedSource source = new(_index);

        bool seeded = source.TrySeed(_block, 1, slot);
        slot.Current!.TryGetAccount(TestItem.AddressA, new Account(3, 100), out Account? account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seeded, Is.True);
            Assert.That(account!.Balance, Is.EqualTo((UInt256)42), "the first transaction's write reads through");
            Assert.That(slot.Current.TryGetAccount(TestItem.AddressB, null, out _), Is.False, "the second transaction is the target and is not in the prefix");
        }

        slot.Disarm();
    }

    [Test]
    public void TheFirstTransaction_HasNoPrefixToSeed()
    {
        StateReadOverlaySlot slot = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(new ChangesetPrefixStateSeedSource(_index).TrySeed(_block, 0, slot), Is.False);
            Assert.That(slot.Current, Is.Null);
        }
    }

    [Test]
    public void AnUncoveredBlock_LeavesTheSlotAlone()
    {
        StateReadOverlaySlot slot = new();
        Block other = Build.A.Block.WithNumber(8).WithTransactions(Build.A.Transaction.TestObject, Build.A.Transaction.TestObject).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(new ChangesetPrefixStateSeedSource(_index).TrySeed(other, 1, slot), Is.False);
            Assert.That(slot.Current, Is.Null);
        }
    }
}
