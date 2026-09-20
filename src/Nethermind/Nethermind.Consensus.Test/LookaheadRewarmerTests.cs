// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Threading;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

[TestFixture]
public class LookaheadRewarmerTests
{
    private static readonly StorageCell SlotA = new(TestItem.AddressA, 1);

    private static PreBlockCaches CreateCaches() => new(new PreBlockCachesConfig(), PrecompileCaches.Empty);

    private static PreBlockCaches.CommittedWriteSet WriteSet(int txIndex, params StorageCell[] slots)
    {
        PreBlockCaches.CommittedWriteSet writes = new(txIndex);
        foreach (StorageCell slot in slots) writes.Slots.Add((slot, UInt256.One));
        return writes;
    }

    [Test]
    public void Commit_marks_only_readers_far_enough_ahead()
    {
        PreBlockCaches caches = CreateCaches();
        BlockCachePreWarmer.LookaheadRewarmer rewarmer = new(transactionCount: 8);
        using (PreBlockCaches.ReadSet reads = caches.BeginReadSet())
        {
            reads.RecordSlot(in SlotA);
            rewarmer.Publish(5, reads);
        }
        using (PreBlockCaches.ReadSet reads = caches.BeginReadSet())
        {
            reads.RecordSlot(in SlotA);
            rewarmer.Publish(2, reads);
        }

        using (PreBlockCaches.CommittedWriteSet writes = WriteSet(1, SlotA)) rewarmer.OnCommitted(writes);

        using ArrayPoolList<int> batch = new(4);
        rewarmer.TakePending(batch, mainThreadTxIndex: 1);
        // tx 2 is next in line for the main thread, so a re-warm could not get ahead of it; tx 5 can.
        Assert.That(batch.AsSpan().ToArray(), Is.EqualTo(new[] { 5 }));
    }

    [Test]
    public void Commit_of_an_unread_key_marks_nothing()
    {
        PreBlockCaches caches = CreateCaches();
        BlockCachePreWarmer.LookaheadRewarmer rewarmer = new(transactionCount: 8);
        using (PreBlockCaches.ReadSet reads = caches.BeginReadSet())
        {
            reads.RecordSlot(in SlotA);
            rewarmer.Publish(5, reads);
        }

        using (PreBlockCaches.CommittedWriteSet writes = WriteSet(1, new StorageCell(TestItem.AddressB, 1))) rewarmer.OnCommitted(writes);

        using ArrayPoolList<int> batch = new(4);
        rewarmer.TakePending(batch, mainThreadTxIndex: 0);
        Assert.That(batch.Count, Is.Zero);
    }

    [Test]
    public void Transactions_the_main_thread_passed_are_dropped_and_a_commit_during_a_rewarm_requeues()
    {
        PreBlockCaches caches = CreateCaches();
        BlockCachePreWarmer.LookaheadRewarmer rewarmer = new(transactionCount: 8);
        using (PreBlockCaches.ReadSet reads = caches.BeginReadSet())
        {
            reads.RecordSlot(in SlotA);
            rewarmer.Publish(6, reads);
        }

        using (PreBlockCaches.CommittedWriteSet writes = WriteSet(1, SlotA)) rewarmer.OnCommitted(writes);
        using ArrayPoolList<int> batch = new(4);
        rewarmer.TakePending(batch, mainThreadTxIndex: 6);
        Assert.That(batch.Count, Is.Zero, "the main thread already reached tx 6");

        using (PreBlockCaches.CommittedWriteSet writes = WriteSet(2, SlotA)) rewarmer.OnCommitted(writes);
        rewarmer.TakePending(batch, mainThreadTxIndex: 2);
        Assert.That(batch.AsSpan().ToArray(), Is.EqualTo(new[] { 6 }), "marked again once it is no longer passed");

        // A commit while tx 6 is being re-warmed dirties it, and completing the re-warm queues it once more.
        using (PreBlockCaches.CommittedWriteSet writes = WriteSet(3, SlotA)) rewarmer.OnCommitted(writes);
        rewarmer.Completed(6);
        batch.Clear();
        rewarmer.TakePending(batch, mainThreadTxIndex: 3);
        Assert.That(batch.AsSpan().ToArray(), Is.EqualTo(new[] { 6 }));
        Assert.That(rewarmer.Rewarmed, Is.EqualTo(1));
    }

    [Test]
    public void Published_write_set_is_signalled_stamped_and_applied_to_the_overlay()
    {
        PreBlockCaches caches = CreateCaches();
        caches.MainTxIndex = 3;
        Account account = TestItem.GenerateRandomAccount();

        PreBlockCaches.CommittedWriteSet writes = caches.BeginWriteSet();
        writes.Accounts.Add((TestItem.AddressA, account));
        writes.Slots.Add((SlotA, 42));
        caches.Publish(writes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(caches.WaitForCommit(TimeSpan.FromSeconds(1), CancellationToken.None), Is.True);
            Assert.That(caches.TryGetCommitted(in SlotA, out _), Is.False, "the overlay is filled by the consumer of the queue, not by the publisher");
            Assert.That(caches.TryDequeueCommitted(out PreBlockCaches.CommittedWriteSet? dequeued), Is.True);
            Assert.That(dequeued!.TxIndex, Is.EqualTo(3));
            caches.ApplyCommitted(dequeued);
            dequeued.Dispose();
            Assert.That(caches.TryGetCommitted(in SlotA, out UInt256 value) && value == 42, Is.True);
            Assert.That(caches.TryGetCommitted(new AddressAsKey(TestItem.AddressA), out Account? committed) && ReferenceEquals(committed, account), Is.True);
        }

        caches.BeginConsumerScope();
        Assert.That(caches.TryGetCommitted(in SlotA, out _), Is.False, "a new block starts from an empty overlay");
        caches.EndConsumerScope();
        Assert.That(caches.WaitForCommit(TimeSpan.Zero, CancellationToken.None), Is.False, "an empty write set is not published");
    }

    [Test]
    public void Empty_write_set_is_dropped_without_a_signal()
    {
        PreBlockCaches caches = CreateCaches();
        caches.Publish(caches.BeginWriteSet());

        Assert.That(caches.WaitForCommit(TimeSpan.Zero, CancellationToken.None), Is.False);
        Assert.That(caches.TryDequeueCommitted(out _), Is.False);
    }
}
