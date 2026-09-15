// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class CoveredBlockTests
{
    private static readonly Account Parent = new(5, 100, TestItem.KeccakA, TestItem.KeccakB);

    private SnapshotableMemColumnsDb<FlatHistoryColumns> _columns = null!;
    private TransactionChangesetIndex _index = null!;
    private Block _seven = null!;
    private Block _eight = null!;

    [SetUp]
    public void SetUp()
    {
        _columns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _index = new TransactionChangesetIndex(_columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        _seven = Build.A.Block.WithNumber(7).WithTransactions(Tx(0), Tx(1), Tx(2)).TestObject;
        _eight = Build.A.Block.WithNumber(8).WithParentHash(_seven.Hash!).WithTransactions(Tx(3), Tx(4)).TestObject;

        Capture(_seven,
            t => t.ReportBalanceChange(TestItem.AddressA, 100, 10),
            t => t.ReportNonceChange(TestItem.AddressB, 5, 6),
            t => t.ReportBalanceChange(TestItem.AddressA, 10, 30));
        Capture(_eight,
            t => t.ReportStorageChange(new StorageCell(TestItem.AddressC, 1), [0x01], [0x09]),
            t => t.ReportBalanceChange(TestItem.AddressA, 30, 40));
    }

    [TearDown]
    public void TearDown() => _columns.Dispose();

    [Test]
    public void ABlockWithEveryRow_Opens_AndAnotherHashOrAShortBlockDoesNot()
    {
        Block sibling = Build.A.Block.WithNumber(7).WithParentHash(TestItem.KeccakC).WithTransactions(Tx(0), Tx(1), Tx(2)).TestObject;
        Block longer = Build.A.Block.WithNumber(7).WithTransactions(Tx(0), Tx(1), Tx(2), Tx(9)).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_index.TryOpenBlock(_seven, out ICoveredBlock? covered), Is.True);
            covered!.Dispose();
            Assert.That(_index.TryOpenBlock(sibling, out _), Is.False, "the rows describe another block at that height");
            Assert.That(_index.TryOpenBlock(longer, out _), Is.False, "a row for every transaction, or nothing");
            Assert.That(_index.TryOpenBlock(Build.A.Block.WithNumber(9).WithTransactions(Tx(0)).TestObject, out _), Is.False, "outside the coverage");
        }
    }

    [Test]
    public void WorkerSeeds_FoldToTheTarget_AndRefoldWhenTheTargetGoesBack()
    {
        Assert.That(_index.TryOpenBlock(_seven, out ICoveredBlock? covered), Is.True);
        using ICoveredBlock block = covered!;
        IPrefixStateSeedSource seeds = block.CreateWorkerSeeds();
        StateReadOverlaySlot slot = new();

        Assert.That(seeds.TrySeed(_seven, 1, slot), Is.True);
        slot.Current!.TryGetAccount(TestItem.AddressA, Parent, out Account? afterFirst);
        Assert.That(seeds.TrySeed(_seven, 3, slot), Is.True);
        slot.Current!.TryGetAccount(TestItem.AddressA, Parent, out Account? afterAll);
        bool nonceKnown = slot.Current!.TryGetAccount(TestItem.AddressB, Parent, out Account? b);
        Assert.That(seeds.TrySeed(_seven, 1, slot), Is.True, "a worker handed an earlier transaction starts over");
        slot.Current!.TryGetAccount(TestItem.AddressA, Parent, out Account? backToFirst);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterFirst!.Balance, Is.EqualTo((UInt256)10));
            Assert.That(afterAll!.Balance, Is.EqualTo((UInt256)30));
            Assert.That(nonceKnown && b!.Nonce == 6, Is.True);
            Assert.That(backToFirst!.Balance, Is.EqualTo((UInt256)10));
            Assert.That(slot.Current.TryGetAccount(TestItem.AddressB, Parent, out _), Is.False, "the second transaction is no longer in the prefix");
        }
    }

    [Test]
    public void WorkerSeeds_RefuseAnotherBlock_AndAnIndexPastTheEnd()
    {
        Assert.That(_index.TryOpenBlock(_seven, out ICoveredBlock? covered), Is.True);
        using ICoveredBlock block = covered!;
        IPrefixStateSeedSource seeds = block.CreateWorkerSeeds();
        StateReadOverlaySlot slot = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seeds.TrySeed(_eight, 1, slot), Is.False);
            Assert.That(seeds.TrySeed(_seven, 4, slot), Is.False);
            Assert.That(seeds.TrySeed(_seven, 3, slot), Is.True, "the state after the last transaction is a valid seed: the rewards run on it");
        }
    }

    [Test]
    public void ACompletedBlock_IsReadThroughByTheNextBlocksWorkers()
    {
        Assert.That(_index.TryOpenBlock(_seven, out ICoveredBlock? seven), Is.True);
        seven!.Complete();
        seven.Dispose();

        Assert.That(_index.TryOpenBlock(_eight, out ICoveredBlock? eight), Is.True);
        using ICoveredBlock block = eight!;
        StateReadOverlaySlot slot = new();
        Assert.That(block.CreateWorkerSeeds().TrySeed(_eight, 1, slot), Is.True);
        IStateReadOverlay view = slot.Current!;

        bool aKnown = view.TryGetAccount(TestItem.AddressA, Parent, out Account? a);
        bool bKnown = view.TryGetAccount(TestItem.AddressB, Parent, out Account? b);
        bool slotKnown = view.TryGetStorage(TestItem.AddressC, 1, out UInt256 slotOne);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(aKnown && a!.Balance == 30, Is.True, "block 7's last write, which block 8's prefix has not changed yet");
            Assert.That(bKnown && b!.Nonce == 6, Is.True, "a key only the earlier block wrote is still answered from memory");
            Assert.That(slotKnown && slotOne == 9, Is.True, "the block's own prefix");
            Assert.That(view.HasStorage(TestItem.AddressC), Is.True);
        }

        Assert.That(block.CreateWorkerSeeds().TrySeed(_eight, 2, slot), Is.True);
        slot.Current!.TryGetAccount(TestItem.AddressA, Parent, out Account? aAfter);
        Assert.That(aAfter!.Balance, Is.EqualTo((UInt256)40), "the block's own write wins over the earlier block's");
    }

    [Test]
    public void ABlockWhoseParentWasNotTraced_StartsFresh()
    {
        Assert.That(_index.TryOpenBlock(_eight, out ICoveredBlock? eight), Is.True);
        using ICoveredBlock block = eight!;
        StateReadOverlaySlot slot = new();
        Assert.That(block.CreateWorkerSeeds().TrySeed(_eight, 1, slot), Is.True);

        Assert.That(slot.Current!.TryGetAccount(TestItem.AddressB, Parent, out _), Is.False, "nothing earlier was published, so the read goes to the parent state");
    }

    private static Transaction Tx(ulong nonce) => Build.A.Transaction.WithNonce(nonce).TestObject;

    private void Capture(Block block, params Action<ITxTracer>[] writes)
    {
        using TransactionChangesetIndex.BlockCapture capture = _index.StartBlock((ulong)block.Number);
        capture.Tracer.StartNewBlockTrace(block);
        for (int i = 0; i < writes.Length; i++)
        {
            ITxTracer tracer = capture.Tracer.StartNewTxTrace(block.Transactions[i]);
            writes[i](tracer);
            capture.Tracer.EndTxTrace();
        }

        capture.Tracer.EndBlockTrace();
        Assert.That(capture.Commit() && _index.TryClaim((ulong)block.Number, (ulong)block.Number), Is.True, "precondition: the block is indexed");
    }
}
