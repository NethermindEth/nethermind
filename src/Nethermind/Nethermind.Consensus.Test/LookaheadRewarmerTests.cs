// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

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
        foreach (StorageCell slot in slots) writes.Slots.Add(slot);
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
    public void Consumer_write_batch_feeds_the_committed_overlay_and_the_queue()
    {
        PreBlockCaches caches = CreateCaches();
        caches.MainTxIndex = 3;
        RecordingWriteBatch inner = new();

        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = caches.WrapCommittedWrites(inner))
        {
            batch.Set(TestItem.AddressA, TestItem.GenerateRandomAccount());
            using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(TestItem.AddressA, 1);
            storage.Set(1, 42);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inner.Accounts, Is.EqualTo(1), "writes still reach the wrapped batch");
            Assert.That(inner.Slots, Is.EqualTo(1));
            Assert.That(caches.TryGetCommitted(in SlotA, out UInt256 value) && value == 42, Is.True);
            Assert.That(caches.TryGetCommitted(new AddressAsKey(TestItem.AddressA), out Account? account) && account is not null, Is.True);
            Assert.That(caches.TryDequeueCommitted(out PreBlockCaches.CommittedWriteSet? writes), Is.True);
            Assert.That(writes!.TxIndex, Is.EqualTo(3));
            Assert.That(writes.Slots.Count, Is.EqualTo(1));
            Assert.That(writes.Accounts.Count, Is.EqualTo(1));
            writes.Dispose();
        }

        caches.BeginConsumerScope();
        Assert.That(caches.TryGetCommitted(in SlotA, out _), Is.False, "a new block starts from an empty overlay");
        caches.EndConsumerScope();
    }

    [Test]
    public void Concurrent_storage_batches_of_one_commit_all_reach_the_write_set()
    {
        PreBlockCaches caches = CreateCaches();
        RecordingWriteBatch inner = new();
        const int Contracts = 32;
        const int SlotsPerContract = 64;

        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = caches.WrapCommittedWrites(inner))
        {
            // Storage batches of one commit are written by the parallel storage-root workers.
            System.Threading.Tasks.Parallel.For(0, Contracts, contract =>
            {
                using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(Address.FromNumber((UInt256)(contract + 1)), SlotsPerContract);
                for (int slot = 0; slot < SlotsPerContract; slot++) storage.Set((UInt256)slot, (UInt256)(slot + 1));
            });
        }

        Assert.That(caches.TryDequeueCommitted(out PreBlockCaches.CommittedWriteSet? writes), Is.True);
        Assert.That(writes!.Slots.Count, Is.EqualTo(Contracts * SlotsPerContract));
        writes.Dispose();
    }

    private sealed class RecordingWriteBatch : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        public int Accounts;
        public int Slots;

        public event System.EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated { add { } remove { } }

        public void Set(Address key, Account? account) => Accounts++;

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) => new RecordingStorageWriteBatch(this);

        public void Dispose() { }

        private sealed class RecordingStorageWriteBatch(RecordingWriteBatch owner) : IWorldStateScopeProvider.IStorageWriteBatch
        {
            public void Set(in UInt256 index, in UInt256 value) => owner.Slots++;
            public void Clear() { }
            public void Dispose() { }
        }
    }
}
