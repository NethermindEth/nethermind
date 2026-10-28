// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
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
        _index = new TransactionChangesetIndex(_columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true }, new TestSpecProvider(Prague.Instance));
        // Difficulty zero, and the processing flag left where a decoded header leaves it: this is the block a trace
        // reads back from the store.
        _seven = Build.A.Block.WithNumber(7).WithDifficulty(0).WithBeneficiary(TestItem.AddressD).WithTransactions(Tx(0), Tx(1), Tx(2)).TestObject;
        _eight = Build.A.Block.WithNumber(8).WithDifficulty(0).WithBeneficiary(TestItem.AddressD).WithParentHash(_seven.Hash!).WithTransactions(Tx(3), Tx(4)).TestObject;

        Capture(_seven,
            t =>
            {
                t.ReportBalanceChange(TestItem.AddressA, 100, 10);
                t.ReportStorageChange(new StorageCell(TestItem.AddressA, 1), [0x01], [0x02]);
            },
            t =>
            {
                t.ReportNonceChange(TestItem.AddressB, 5, 6);
                t.ReportStorageChange(new StorageCell(TestItem.AddressB, 1), [0x05], [0x06]);
            },
            t => t.ReportBalanceChange(TestItem.AddressA, 10, 30));
        Capture(_eight,
            t => t.ReportStorageChange(new StorageCell(TestItem.AddressC, 1), [0x01], [0x09]),
            t => t.ReportBalanceChange(TestItem.AddressA, 30, 40));
    }

    [TearDown]
    public void TearDown() => _columns.Dispose();

    [Test]
    public void ABlockWithEveryRow_Opens_AndAnotherHashDoesNot()
    {
        Block sibling = Build.A.Block.WithNumber(7).WithParentHash(TestItem.KeccakC).WithTransactions(Tx(0), Tx(1), Tx(2)).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_index.TryOpenBlock(_seven, out ICoveredBlock? covered), Is.True);
            covered!.Dispose();
            Assert.That(_index.TryOpenBlock(sibling, out _), Is.False, "the rows describe another block at that height");
            Assert.That(_index.TryOpenBlock(Build.A.Block.WithNumber(9).WithTransactions(Tx(0)).TestObject, out _), Is.False, "outside the coverage");
        }
    }

    [Test]
    public void ABlockMissingTheRowOfItsLastTransaction_IsNotOpened()
    {
        Assert.That(_index.TryOpenBlock(_seven, out ICoveredBlock? whole), Is.True, "precondition: the block opens while every row is there");
        whole!.Dispose();
        Span<byte> key = stackalloc byte[ChangesetKeyLayout.RowKeyLength];
        ChangesetKeyLayout.WriteRowKey(key, (ulong)_seven.Number, 2);
        _columns.GetColumnDb(FlatHistoryColumns.TransactionChangesets).Remove(key);

        bool opened = _index.TryOpenBlock(_seven, out ICoveredBlock? covered);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opened, Is.False, "the hash still matches, so only the row count can refuse it: a row for every transaction, or nothing");
            Assert.That(covered, Is.Null);
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
        IStateReadOverlay prefix = slot.Current!;
        IStorageReadOverlay earlier = slot.ParentState!;

        bool aKnown = earlier.TryGetStorage(TestItem.AddressA, 1, out UInt256 a);
        bool bKnown = earlier.TryGetStorage(TestItem.AddressB, 1, out UInt256 b);
        bool slotKnown = prefix.TryGetStorage(TestItem.AddressC, 1, out UInt256 slotOne);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(aKnown && a == 2, Is.True, "block 7's write, which block 8's prefix has not changed");
            Assert.That(bKnown && b == 6, Is.True, "a slot only the earlier block wrote is still answered from memory");
            Assert.That(earlier.HasStorage(TestItem.AddressB), Is.True);
            Assert.That(slotKnown && slotOne == 9, Is.True, "the block's own prefix");
            Assert.That(prefix.HasStorage(TestItem.AddressC), Is.True);
        }
    }

    [Test]
    public void AnAddressTheBlockCanWriteAfterItsTransactions_IsNeverAnsweredFromTheChain()
    {
        Block seven = Build.A.Block.WithNumber(7).WithDifficulty(0).WithBeneficiary(TestItem.AddressB).WithTransactions(Tx(0), Tx(1), Tx(2))
            .WithWithdrawals([new Withdrawal { Address = TestItem.AddressA, AmountInGwei = 1 }]).TestObject;
        seven.Header.Hash = _seven.Hash;
        Assert.That(_index.TryOpenBlock(seven, out ICoveredBlock? covered), Is.True);
        covered!.Complete();
        covered.Dispose();

        Assert.That(_index.TryOpenBlock(_eight, out ICoveredBlock? eight), Is.True);
        using ICoveredBlock block = eight!;
        StateReadOverlaySlot slot = new();
        Assert.That(block.CreateWorkerSeeds().TrySeed(_eight, 1, slot), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slot.ParentState!.TryGetStorage(TestItem.AddressA, 1, out _), Is.False, "a withdrawal recipient: the withdrawal credited it after the transactions, so the parent state must answer");
            Assert.That(slot.ParentState.TryGetStorage(TestItem.AddressB, 1, out _), Is.False, "the beneficiary: the reward reached it after the transactions");
        }
    }

    [Test]
    public void ABlockBeforeTheMerge_IsNotChained()
    {
        Block seven = Build.A.Block.WithNumber(7).WithDifficulty(17).WithBeneficiary(TestItem.AddressD).WithTransactions(Tx(0), Tx(1), Tx(2)).TestObject;
        seven.Header.Hash = _seven.Hash;
        Assert.That(_index.TryOpenBlock(seven, out ICoveredBlock? covered), Is.True);
        covered!.Complete();
        covered.Dispose();

        Assert.That(_index.TryOpenBlock(_eight, out ICoveredBlock? eight), Is.True);
        using ICoveredBlock block = eight!;
        StateReadOverlaySlot slot = new();
#pragma warning disable NUnit2045 // Successful seeding is a prerequisite for inspecting the seeded slot.
        Assert.That(block.CreateWorkerSeeds().TrySeed(_eight, 1, slot), Is.True);
#pragma warning restore NUnit2045

        Assert.That(slot.ParentState, Is.Null, "an uncle or a real reward could have reached anyone, so nothing of that block is chained");
    }

    [Test]
    public void ABlockReadBackFromTheStore_StillChains()
    {
#pragma warning disable NUnit2045 // Keep prerequisite acquisition outside a grouped scope; its output is used by later assertions or subsequent setup/disposal.
        Assert.That(_seven.Header.IsPostMerge, Is.False, "precondition: the flag a decoded header does not carry");
#pragma warning restore NUnit2045

        Assert.That(_index.TryOpenBlock(_seven, out ICoveredBlock? covered), Is.True);
        covered!.Complete();
        covered.Dispose();

        Assert.That(_index.TryOpenBlock(_eight, out ICoveredBlock? eight), Is.True);
        using ICoveredBlock block = eight!;
        StateReadOverlaySlot slot = new();
#pragma warning disable NUnit2045 // The call establishes an output or mutable state needed by later assertions; keep the success prerequisite fail-fast.
        Assert.That(block.CreateWorkerSeeds().TrySeed(_eight, 1, slot), Is.True);
#pragma warning restore NUnit2045

        Assert.That(slot.ParentState!.TryGetStorage(TestItem.AddressB, 1, out UInt256 b), Is.True, "zero difficulty is what says proof of stake for a block read back from the store");
        Assert.That(b, Is.EqualTo((UInt256)6));
    }

    [Test]
    public void ABlockOfAChainWhoseProcessingIsNotDescribed_IsNotChained()
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex aura = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true },
            new SingleReleaseSpecProvider(Prague.Instance, 1, 1) { SealEngine = SealEngineType.AuRa });
        CaptureInto(aura, _seven, t => t.ReportBalanceChange(TestItem.AddressA, 100, 10), t => t.ReportNonceChange(TestItem.AddressB, 5, 6), t => t.ReportBalanceChange(TestItem.AddressA, 10, 30));
        CaptureInto(aura, _eight, t => t.ReportStorageChange(new StorageCell(TestItem.AddressC, 1), [0x01], [0x09]), t => t.ReportBalanceChange(TestItem.AddressA, 30, 40));

        Assert.That(aura.TryOpenBlock(_seven, out ICoveredBlock? covered), Is.True);
        covered!.Complete();
        covered.Dispose();

        Assert.That(aura.TryOpenBlock(_eight, out ICoveredBlock? eight), Is.True);
        using ICoveredBlock block = eight!;
        StateReadOverlaySlot slot = new();
#pragma warning disable NUnit2045 // Successful seeding is a prerequisite for inspecting the seeded slot.
        Assert.That(block.CreateWorkerSeeds().TrySeed(_eight, 0, slot), Is.True);
#pragma warning restore NUnit2045

        Assert.That(slot.ParentState, Is.Null, "the withdrawals of such a chain are a contract call whose writes no spec property names, so nothing of its blocks is chained");
    }

    [Test]
    public void ABlockCarryingARowTheCodecCannotRead_IsNotOpened()
    {
        long refusedBefore = Nethermind.State.Flat.Metrics.UnreadableTransactionChangesetRows;
        Truncate(_seven, 1);

        bool opened = _index.TryOpenBlock(_seven, out ICoveredBlock? covered);
        bool otherBlockStillOpens = _index.TryOpenBlock(_eight, out ICoveredBlock? eight);
        eight?.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opened, Is.False, "a row that only failed once a worker reached it would cost a prefix replay per transaction of the block; refusing the block costs the one replay the node did before the index existed");
            Assert.That(covered, Is.Null);
            Assert.That(Nethermind.State.Flat.Metrics.UnreadableTransactionChangesetRows, Is.GreaterThan(refusedBefore), "a node whose column carries damage answers correctly and slowly, so the refusal is counted");
            Assert.That(otherBlockStillOpens, Is.True, "only the block holding the row is refused");
        }
    }

    [Test]
    public void AnOverlayStillArmed_IsNotGivenToAnotherBlock_AfterItsBlockIsDisposed()
    {
        Assert.That(_index.TryOpenBlock(_seven, out ICoveredBlock? seven), Is.True);
        StateReadOverlaySlot armed = new();
        Assert.That(seven!.CreateWorkerSeeds().TrySeed(_seven, 3, armed), Is.True);
        seven.Dispose();

        for (int i = 0; i < 4; i++)
        {
            Assert.That(_index.TryOpenBlock(_eight, out ICoveredBlock? eight), Is.True);
            StateReadOverlaySlot slot = new();
            Assert.That(eight!.CreateWorkerSeeds().TrySeed(_eight, 2, slot), Is.True);
            slot.Disarm();
            eight.Dispose();
        }

        bool aKnown = armed.Current!.TryGetAccount(TestItem.AddressA, Parent, out Account? a);
        bool bKnown = armed.Current.TryGetAccount(TestItem.AddressB, Parent, out Account? b);
        bool slotKnown = armed.Current.TryGetStorage(TestItem.AddressC, 1, out _);
        armed.Disarm();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(aKnown && a!.Balance == 30, Is.True, "the lease keeps block 7's overlay out of the spares until the slot lets go of it");
            Assert.That(bKnown && b!.Nonce == 6, Is.True);
            Assert.That(slotKnown, Is.False, "block 8's write never reached the overlay block 7 is still read through");
        }
    }

    [Test]
    public void ABlockOpenedAfterAnotherWasDisposed_SeesOnlyItsOwnPrefix()
    {
        Assert.That(_index.TryOpenBlock(_seven, out ICoveredBlock? seven), Is.True);
        StateReadOverlaySlot first = new();
        Assert.That(seven!.CreateWorkerSeeds().TrySeed(_seven, 3, first), Is.True);
        first.Disarm();
        seven.Dispose();

        Assert.That(_index.TryOpenBlock(_eight, out ICoveredBlock? eight), Is.True);
        using ICoveredBlock block = eight!;
        StateReadOverlaySlot slot = new();
        Assert.That(block.CreateWorkerSeeds().TrySeed(_eight, 2, slot), Is.True);
        IStateReadOverlay prefix = slot.Current!;

        bool aKnown = prefix.TryGetAccount(TestItem.AddressA, Parent, out Account? a);
        bool cKnown = prefix.TryGetStorage(TestItem.AddressC, 1, out UInt256 c);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(aKnown && a!.Balance == 40, Is.True);
            Assert.That(cKnown && c == 9, Is.True);
            Assert.That(prefix.TryGetAccount(TestItem.AddressB, Parent, out _), Is.False, "only block 7 wrote the account");
            Assert.That(prefix.TryGetStorage(TestItem.AddressA, 1, out _), Is.False, "only block 7 wrote the slot");
            Assert.That(prefix.HasStorage(TestItem.AddressB), Is.False);
        }

        slot.Disarm();
    }

    [Test]
    public void AWorkerOfADisposedBlock_IsRefused()
    {
        Assert.That(_index.TryOpenBlock(_seven, out ICoveredBlock? seven), Is.True);
        IPrefixStateSeedSource seeds = seven!.CreateWorkerSeeds();
        seven.Dispose();

        StateReadOverlaySlot slot = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(seeds.TrySeed(_seven, 2, slot), Is.False, "its overlay may already be folding another block");
            Assert.That(slot.Current, Is.Null);
        }
    }

    [Test]
    public void ABlockWhoseParentWasNotTraced_StartsFresh()
    {
        Assert.That(_index.TryOpenBlock(_eight, out ICoveredBlock? eight), Is.True);
        using ICoveredBlock block = eight!;
        StateReadOverlaySlot slot = new();
#pragma warning disable NUnit2045 // Successful seeding is a prerequisite for inspecting the seeded slot.
        Assert.That(block.CreateWorkerSeeds().TrySeed(_eight, 1, slot), Is.True);
#pragma warning restore NUnit2045

        Assert.That(slot.ParentState, Is.Null, "nothing earlier was published, so the read goes to the parent state");
    }

    private void Truncate(Block block, ushort transactionIndex)
    {
        Span<byte> key = stackalloc byte[ChangesetKeyLayout.RowKeyLength];
        ChangesetKeyLayout.WriteRowKey(key, (ulong)block.Number, transactionIndex);
        IDb column = _columns.GetColumnDb(FlatHistoryColumns.TransactionChangesets);
        byte[] row = column.Get(key)!;
        column.Set(key, row[..^1]);
    }

    private static Transaction Tx(ulong nonce) => Build.A.Transaction.WithNonce(nonce).TestObject;

    private void Capture(Block block, params Action<ITxTracer>[] writes) => CaptureInto(_index, block, writes);

    private static void CaptureInto(TransactionChangesetIndex index, Block block, params Action<ITxTracer>[] writes)
    {
        using TransactionChangesetIndex.BlockCapture capture = index.StartBlock((ulong)block.Number);
        capture.Tracer.StartNewBlockTrace(block);
        for (int i = 0; i < writes.Length; i++)
        {
            ITxTracer tracer = capture.Tracer.StartNewTxTrace(block.Transactions[i]);
            writes[i](tracer);
            capture.Tracer.EndTxTrace();
        }

        capture.Tracer.EndBlockTrace();
        Assert.That(capture.Commit() && index.TryClaim((ulong)block.Number, (ulong)block.Number), Is.True, "precondition: the block is indexed");
    }
}
