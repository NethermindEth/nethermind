// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// <see cref="EvmObjectPool{T}"/> fronts a shared queue with a per-thread free list, so the invariants
/// worth pinning are that an item is never handed to two renters and never silently lost, and that the
/// shared tier's bound still holds once the local tier overflows.
/// </summary>
/// <remarks>
/// Every test pools its own item type, and creates exactly one pool over it. The per-thread free list is
/// static per closed generic type, so tests sharing a type would see each other's cached items — which
/// is also what the pool's own debug assertion forbids.
/// </remarks>
public class EvmObjectPoolTests
{
    private class Item(int id)
    {
        public int Id { get; } = id;
    }

    private sealed class EmptyItem(int id) : Item(id);
    private sealed class LocalItem(int id) : Item(id);
    private sealed class OverflowItem(int id) : Item(id);
    private sealed class BoundItem(int id) : Item(id);
    private sealed class CrossThreadItem(int id) : Item(id);
    private sealed class ChurnItem(int id) : Item(id);

    [Test]
    public void Empty_pool_reports_no_item()
    {
        EvmObjectPool<EmptyItem> pool = new(localCapacity: 4);

        Assert.That(pool.TryDequeue(out EmptyItem? item), Is.False);
        Assert.That(item, Is.Null);
    }

    [Test]
    public void Returned_items_come_back_on_the_same_thread()
    {
        EvmObjectPool<LocalItem> pool = new(localCapacity: 4);
        LocalItem first = new(1);
        LocalItem second = new(2);

        pool.Enqueue(first);
        pool.Enqueue(second);

        // LIFO: the local free list hands back the most recently returned item first, which is what
        // keeps a call frame's stack hot in cache across the rent/return cycle.
        Assert.That(pool.TryDequeue(out LocalItem? a), Is.True);
        Assert.That(a, Is.SameAs(second));
        Assert.That(pool.TryDequeue(out LocalItem? b), Is.True);
        Assert.That(b, Is.SameAs(first));
        Assert.That(pool.TryDequeue(out LocalItem? _), Is.False);
    }

    [Test]
    public void Local_overflow_falls_through_to_the_shared_tier()
    {
        const int localCapacity = 2;
        const int returned = 5;
        EvmObjectPool<OverflowItem> pool = new(localCapacity);

        for (int i = 0; i < returned; i++)
        {
            pool.Enqueue(new OverflowItem(i));
        }

        HashSet<int> seen = [];
        for (int i = 0; i < returned; i++)
        {
            Assert.That(pool.TryDequeue(out OverflowItem? item), Is.True, $"item {i} was lost");
            Assert.That(seen.Add(item!.Id), Is.True, $"item {item.Id} was handed out twice");
        }

        Assert.That(pool.TryDequeue(out OverflowItem? _), Is.False);
        Assert.That(seen, Has.Count.EqualTo(returned));
    }

    [Test]
    public void Shared_tier_stops_retaining_at_its_bound()
    {
        const int localCapacity = 1;
        const int maxShared = 3;
        EvmObjectPool<BoundItem> pool = new(localCapacity, maxShared);

        for (int i = 0; i < 50; i++)
        {
            pool.Enqueue(new BoundItem(i));
        }

        int drained = 0;
        while (pool.TryDequeue(out BoundItem? _))
        {
            drained++;
        }

        // The local tier plus the shared tier and nothing more: excess returns are dropped for the GC.
        Assert.That(drained, Is.EqualTo(localCapacity + maxShared));
    }

    [Test]
    public void Items_overflowed_on_one_thread_are_rentable_on_another()
    {
        EvmObjectPool<CrossThreadItem> pool = new(localCapacity: 1);
        CrossThreadItem overflowed = new(42);
        CrossThreadItem? seen = null;

        // Dedicated threads, not the thread pool: two Task.Run bodies can land on the same thread,
        // where the producer's local free list would satisfy the rent and prove nothing.
        RunOnNewThread(() =>
        {
            pool.Enqueue(new CrossThreadItem(0)); // fills the producer's single local slot
            pool.Enqueue(overflowed);             // overflows to the shared tier
        });

        RunOnNewThread(() =>
        {
            pool.TryDequeue(out CrossThreadItem? item);
            seen = item;
        });

        Assert.That(seen, Is.SameAs(overflowed));
    }

    [Test]
    public void Concurrent_churn_never_duplicates_or_loses_an_item()
    {
        const int threads = 8;
        const int itemsPerThread = 64;
        const int rounds = 500;
        const int localCapacity = 4;
        const int maxShared = threads * itemsPerThread;

        EvmObjectPool<ChurnItem> pool = new(localCapacity, maxShared);

        // A rented item is marked for as long as it is held, so a second concurrent hand-out of the
        // same instance finds the mark already set.
        ConcurrentDictionary<ChurnItem, byte> rented = new();
        int duplicates = 0;
        int allocated = 0;
        int allocatedAfterWarmup = -1;

        Parallel.For(0, threads, _ =>
        {
            ChurnItem[] held = new ChurnItem[itemsPerThread];
            for (int round = 0; round < rounds; round++)
            {
                // Round 0 necessarily allocates the whole concurrent working set against an empty pool,
                // so measure recycling from round 1 on rather than against a cumulative budget.
                if (round == 1)
                {
                    Interlocked.CompareExchange(ref allocatedAfterWarmup, Volatile.Read(ref allocated), -1);
                }

                for (int i = 0; i < itemsPerThread; i++)
                {
                    if (!pool.TryDequeue(out ChurnItem? item))
                    {
                        item = new ChurnItem(Interlocked.Increment(ref allocated));
                    }

                    if (!rented.TryAdd(item!, 0))
                    {
                        Interlocked.Increment(ref duplicates);
                    }

                    held[i] = item!;
                }

                for (int i = 0; i < itemsPerThread; i++)
                {
                    // Unmark before returning, so the next renter never observes a stale mark.
                    rented.TryRemove(held[i], out byte _);
                    pool.Enqueue(held[i]);
                    held[i] = null!;
                }
            }
        });

        Assert.That(duplicates, Is.Zero, "the same instance was rented twice concurrently");

        // A draining thread reaches the shared tier plus its own local list; the other threads' local
        // lists are private to them by design, so this is an upper bound, not an exact count.
        int drained = 0;
        while (pool.TryDequeue(out ChurnItem? _))
        {
            drained++;
        }

        Assert.That(drained, Is.LessThanOrEqualTo(maxShared + localCapacity));

        // Without recycling this workload would allocate threads * itemsPerThread * rounds instances.
        // Assert on the steady state rather than the cumulative total: the warm-up round alone is
        // entitled to the full working set, so a cumulative bound would really be measuring how often
        // TryDequeueShared loses the publish race - a rate, not the invariant under test.
        int steadyStateAllocations = Volatile.Read(ref allocated) - Volatile.Read(ref allocatedAfterWarmup);
        Assert.That(allocatedAfterWarmup, Is.GreaterThanOrEqualTo(0), "warm-up snapshot was never taken");
        Assert.That(steadyStateAllocations, Is.LessThan(threads * itemsPerThread / 4),
            "the pool kept allocating after warm-up instead of recycling");
    }

    private static void RunOnNewThread(ThreadStart body)
    {
        Thread thread = new(body) { IsBackground = true };
        thread.Start();
        Assert.That(thread.Join(TimeSpan.FromSeconds(30)), Is.True, "worker thread did not finish");
    }
}
