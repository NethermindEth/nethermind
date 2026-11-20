// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Comparers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.TxPool.Test.FrameTxFilterTestPools;

namespace Nethermind.TxPool.Test.Collections;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class TxDistinctSortedPoolTests
{
    private static readonly Address Sender = TestItem.AddressA;
    private const ulong SharedNonce = 5;

    private static MempoolTransaction AccountTx(ulong nonce, uint gasBottleneck, Hash256 hash) => new(Build.A.Transaction
        .WithNonce(nonce)
        .WithSenderAddress(Sender)
        .WithHash(hash)
        .TestObject, null)
    { GasBottleneck = gasBottleneck };

    private static MempoolTransaction KeyedTx() => new(Build.A.Transaction
        .WithType(TxType.FrameTx)
        .WithNonce(SharedNonce)
        .WithNonceKeys(0xbeef)
        .WithSenderAddress(Sender)
        .WithHash(TestItem.KeccakB)
        .TestObject, null)
    { GasBottleneck = 50 };

    /// <summary>Marks <paramref name="keyedTx"/> for removal and reprices <paramref name="sibling"/> in the same
    /// pass, which is what <c>UpdateGasBottleneckAndMarkForEviction</c> does when both share a nonce.</summary>
    private static void MarkKeyedAndReprice(TxDistinctSortedPool pool, MempoolTransaction keyedTx, MempoolTransaction sibling, uint siblingGasBottleneck) =>
        pool.UpdatePool(Substitute.For<IAccountStateProvider>(),
            (in AccountStruct _, EnhancedSortedSet<MempoolTransaction> bucket, ref MempoolTransaction? lastElement, TxDistinctSortedPool.UpdateTransactionDelegate updateTx) =>
            {
                updateTx(bucket, keyedTx, changedGasBottleneck: null, lastElement);
                updateTx(bucket, sibling, (UInt256)siblingGasBottleneck, lastElement);
            });

    private static void Reprice(TxDistinctSortedPool pool, MempoolTransaction tx, uint gasBottleneck) =>
        pool.UpdatePool(Substitute.For<IAccountStateProvider>(),
            (in AccountStruct _, EnhancedSortedSet<MempoolTransaction> bucket, ref MempoolTransaction? lastElement, TxDistinctSortedPool.UpdateTransactionDelegate updateTx) =>
                updateTx(bucket, tx, (UInt256)gasBottleneck, lastElement));

    /// <summary>
    /// A keyed transaction's <c>Nonce</c> is a sequence in its own domain, so it can tie an account-nonce
    /// transaction of the same sender and leave the bucket order to the gas-bottleneck comparer. Repricing that
    /// sibling below it inside the pass moves an ordering key the later bucket removal walks past, so the removal
    /// fails — the account-domain count must follow the bucket, not the attempt.
    /// </summary>
    [Test]
    public void Keeps_the_account_domain_count_when_a_reprice_blocks_the_bucket_removal()
    {
        MempoolTransaction n4 = AccountTx(4, 110, TestItem.KeccakA);
        MempoolTransaction accountTx = AccountTx(SharedNonce, 100, TestItem.KeccakC);
        MempoolTransaction keyedTx = KeyedTx();
        MempoolTransaction n6 = AccountTx(6, 90, TestItem.KeccakD);
        MempoolTransaction n7 = AccountTx(7, 80, TestItem.KeccakE);

        TxDistinctSortedPool pool = Pool(blobs: false, n4, accountTx, keyedTx, n6, n7);
        Assert.That(pool.GetAccountDomainBucketCount(Sender), Is.EqualTo(4), "only the account-nonce entries fill the sender's nonce window");

        MarkKeyedAndReprice(pool, keyedTx, accountTx, siblingGasBottleneck: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.GetBucketCount(Sender), Is.EqualTo(5), "the failed bucket removal leaves the keyed entry behind");
            Assert.That(pool.GetAccountDomainBucketCount(Sender), Is.EqualTo(4),
                "an entry the bucket still holds must stay counted, or GapNonceFilter admits a nonce nothing can fill");
        }
    }

    /// <summary>
    /// The entry stranded above is out of the pool's key map but still in the bucket, so the only route that ever
    /// takes it out of the bucket is capacity eviction's fallback, which removes from the bucket directly. The
    /// count has to drop there too, or it outlives the bucket and under-reports the sender's nonce window forever.
    /// </summary>
    [Test]
    public void Drops_the_account_domain_count_when_capacity_eviction_sweeps_the_stranded_entry()
    {
        MempoolTransaction n4 = AccountTx(4, 110, TestItem.KeccakA);
        MempoolTransaction accountTx = AccountTx(SharedNonce, 100, TestItem.KeccakC);
        MempoolTransaction keyedTx = KeyedTx();
        MempoolTransaction n6 = AccountTx(6, 90, TestItem.KeccakD);
        MempoolTransaction n7 = AccountTx(7, 80, TestItem.KeccakE);

        TxDistinctSortedPool pool = Pool(blobs: false, n4, accountTx, keyedTx, n6, n7);
        MarkKeyedAndReprice(pool, keyedTx, accountTx, siblingGasBottleneck: 10);

        // A later head restores the sibling's bottleneck, so the bucket order is walkable again, and removing the
        // entries above the stranded one makes it the bucket's worst value and so the next eviction's target.
        Reprice(pool, accountTx, gasBottleneck: 100);
        pool.TryRemove(n7.Hash!);
        pool.TryRemove(n6.Hash!);

        for (int i = 0; i < 5; i++)
        {
            Hash256 hash = Keccak.Compute($"filler{i}");
            MempoolTransaction filler = new(Build.A.Transaction
                .WithNonce(0)
                .WithSenderAddress(new Address(hash.Bytes[..20]))
                .WithHash(hash)
                .TestObject, null)
            { GasBottleneck = 1000 };
            pool.TryInsert(filler.Hash!, filler);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.GetBucketCount(Sender), Is.EqualTo(1), "eviction swept the stranded entry out of the bucket");
            Assert.That(pool.GetAccountDomainBucketCount(Sender), Is.EqualTo(1),
                "the keyed count must not survive the entry, or the sender's nonce window shrinks by one for good");
        }
    }

    /// <summary>The stranded entry above leaves the key map without leaving the bucket, so only the key-map removal
    /// can report that its hash is gone.</summary>
    [Test]
    public void Counts_a_removal_from_the_key_map_that_leaves_the_bucket_untouched()
    {
        MempoolTransaction accountTx = AccountTx(SharedNonce, 100, TestItem.KeccakC);
        MempoolTransaction keyedTx = KeyedTx();
        TxDistinctSortedPool pool = Pool(blobs: false, AccountTx(4, 110, TestItem.KeccakA), accountTx, keyedTx,
            AccountTx(6, 90, TestItem.KeccakD), AccountTx(7, 80, TestItem.KeccakE));
        long before = pool.GetRemovalGeneration(Sender);

        MarkKeyedAndReprice(pool, keyedTx, accountTx, siblingGasBottleneck: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.ContainsKey(keyedTx.Hash!), Is.False, "precondition: the key map lost the entry");
            Assert.That(pool.GetBucketCount(Sender), Is.EqualTo(5), "precondition: the bucket kept it");
            Assert.That(pool.GetRemovalGeneration(Sender), Is.Not.EqualTo(before));
        }
    }

    /// <summary>Capacity eviction's fallback takes the stranded entry out of the bucket alone, and then evicts a
    /// worse transaction of another sender, so only the bucket removal can report this sender's loss.</summary>
    [Test]
    public void Counts_a_removal_from_the_bucket_alone()
    {
        MempoolTransaction n4 = AccountTx(4, 110, TestItem.KeccakA);
        MempoolTransaction accountTx = AccountTx(SharedNonce, 100, TestItem.KeccakC);
        MempoolTransaction keyedTx = KeyedTx();
        MempoolTransaction n6 = AccountTx(6, 90, TestItem.KeccakD);
        MempoolTransaction n7 = AccountTx(7, 80, TestItem.KeccakE);

        TxDistinctSortedPool pool = Pool(blobs: false, n4, accountTx, keyedTx, n6, n7);
        MarkKeyedAndReprice(pool, keyedTx, accountTx, siblingGasBottleneck: 10);
        Reprice(pool, accountTx, gasBottleneck: 100);
        pool.TryRemove(n7.Hash!);
        pool.TryRemove(n6.Hash!);
        long before = pool.GetRemovalGeneration(Sender);

        MempoolTransaction victim = OtherSenderTx("victim", gasBottleneck: 60);
        pool.TryInsert(victim.Hash!, victim);
        for (int i = 0; i < 4; i++)
        {
            MempoolTransaction filler = OtherSenderTx($"filler{i}", gasBottleneck: 1000);
            pool.TryInsert(filler.Hash!, filler);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.GetBucketCount(Sender), Is.EqualTo(2), "precondition: eviction swept the stranded entry out of the bucket");
            Assert.That(pool.ContainsKey(victim.Hash!), Is.False, "precondition: the key-map eviction took the other sender's transaction");
            Assert.That(pool.GetRemovalGeneration(Sender), Is.Not.EqualTo(before));
        }
    }

    /// <summary>
    /// A full pool whose worst-value heap is empty, or holds only a value that is neither in the key map nor in a
    /// bucket, has nothing to evict. The insert that takes it over capacity and the capacity repair after it must
    /// give up instead of retrying forever under the pool lock.
    /// </summary>
    [Test]
    public void Eviction_gives_up_when_no_worst_value_is_left([Values] bool heapHoldsAValueOutsideThePool)
    {
        MempoolTransaction n5 = AccountTx(5, 100, TestItem.KeccakA);
        MempoolTransaction n6 = AccountTx(6, 90, TestItem.KeccakB);
        MempoolTransaction outside = OtherSenderTx("outside", gasBottleneck: 1);
        HeapEditablePool pool = new(capacity: 2);
        pool.TryInsert(n5.Hash!, n5);
        pool.TryInsert(n6.Hash!, n6);
        pool.ReplaceWorstValues(heapHoldsAValueOutsideThePool ? outside : null);

        Assert.That(pool.TryGetLast(out MempoolTransaction? last) ? last : null, Is.SameAs(heapHoldsAValueOutsideThePool ? outside : null),
            "precondition: the heap holds the value outside the pool or nothing");

        // A lower nonce sorts below the bucket's worst value, so the heap stays as built while the pool goes over capacity.
        MempoolTransaction n4 = AccountTx(4, 110, TestItem.KeccakC);

        // A spinning eviction holds the pool lock for good, so it runs on its own thread behind a bounded wait.
        Task<bool> overCapacity = Task.Factory.StartNew(() =>
        {
            bool inserted = pool.TryInsert(n4.Hash!, n4);

            // A pass over the pool repairs capacity before anything else.
            Reprice(pool, n5, gasBottleneck: 100);
            return inserted;
        }, TaskCreationOptions.LongRunning);

#pragma warning disable NUnit2045 // Completion must be confirmed before reading the result; .Result has no timeout.
        Assert.That(overCapacity.Wait(TimeSpan.FromSeconds(10)), Is.True, "eviction must give up when no worst value is left");
#pragma warning restore NUnit2045
        using (Assert.EnterMultipleScope())
        {
            Assert.That(overCapacity.Result, Is.True);
            Assert.That(pool.Count, Is.EqualTo(3), "nothing is left to evict, so the pool stays over capacity");
            Assert.That(pool.TryGetLast(out _), Is.False);
        }
    }

    /// <summary>A null change asks the update to remove the transaction. The update holds the pool lock already, so
    /// the removal must not take it again, and must leave the pool as <c>TryRemove</c> does, event included.</summary>
    [Test]
    public void Update_removes_a_transaction_whose_change_is_null([Values] bool wholePool)
    {
        MempoolTransaction kept = AccountTx(4, 110, TestItem.KeccakA);
        MempoolTransaction dropped = AccountTx(5, 100, TestItem.KeccakC);
        MempoolTransaction other = OtherSenderTx("other", gasBottleneck: 1000);
        TxDistinctSortedPool pool = Pool(blobs: false, kept, dropped, other);
        List<(MempoolTransaction Tx, bool Evicted)> removed = [];
        pool.Removed += (_, e) => removed.Add((e.Value, e.Evicted));

        // In the whole-pool pass one bucket shrinks and the other one goes while the pool walks its buckets.
        IEnumerable<(MempoolTransaction Tx, Action<MempoolTransaction>? Change)> MarkForRemoval(AddressAsKey group, IReadOnlySortedSet<MempoolTransaction> _) =>
            [((Address)group == Sender ? dropped : other, null)];

        if (wholePool) pool.UpdatePool(MarkForRemoval);
        else pool.UpdateGroup(Sender, MarkForRemoval);

        (MempoolTransaction, bool)[] expected = wholePool ? [(dropped, false), (other, false)] : [(dropped, false)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.EquivalentTo(expected));
            Assert.That(pool.ContainsKey(dropped.Hash!), Is.False);
            Assert.That(pool.GetBucketCount(Sender), Is.EqualTo(1));
            Assert.That(pool.ContainsBucket(other.SenderAddress!), Is.EqualTo(!wholePool));
            Assert.That(pool.TryGetLast(out MempoolTransaction? last) ? last : null, Is.SameAs(kept));
        }
    }

    /// <summary>A full pool makes room for a better transaction by evicting its worst one, and reports it evicted.</summary>
    [Test]
    public void Capacity_eviction_takes_the_worst_transaction_and_reports_it_evicted()
    {
        MempoolTransaction worst = OtherSenderTx("worst", gasBottleneck: 10);
        MempoolTransaction better = OtherSenderTx("better", gasBottleneck: 20);
        MempoolTransaction best = OtherSenderTx("best", gasBottleneck: 30);
        TxDistinctSortedPool pool = Pool(blobs: false, worst);
        pool.TryInsert(better.Hash!, better);
        List<(string Event, MempoolTransaction Tx, bool Evicted)> trace = [];
        pool.Inserted += (_, e) => trace.Add(("Inserted", e.Value, false));
        pool.Removed += (_, e) => trace.Add(("Removed", e.Value, e.Evicted));

        bool inserted = pool.TryInsert(best.Hash!, best, out MempoolTransaction? evicted);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inserted, Is.True);
            Assert.That(evicted, Is.SameAs(worst));
            Assert.That(trace, Is.EqualTo(new[] { ("Inserted", best, false), ("Removed", worst, true) }));
            Assert.That(pool.TryGetLast(out MempoolTransaction? last) ? last : null, Is.SameAs(better));
        }
    }

    /// <summary>
    /// The bucket's ordered copy must walk in the tree's own order, including after a reprice leaves equal-nonce
    /// entries out of comparer order.
    /// </summary>
    [Test]
    public void Bucket_walk_matches_the_sorted_set_through_inserts_removals_and_reprices([Range(1, 20)] int seed)
    {
        Random random = new(seed);
        MempoolTransaction[] candidates = new MempoolTransaction[24];
        for (int i = 0; i < candidates.Length; i++)
        {
            ulong nonce = (ulong)(i % 8);
            uint gasBottleneck = (uint)random.Next(1, 200);
            Hash256 hash = Keccak.Compute($"candidate{i}");
            candidates[i] = i % 3 == 0
                ? new MempoolTransaction(Build.A.Transaction.WithType(TxType.FrameTx).WithNonce(nonce).WithNonceKeys((ulong)(0xbeef + i))
                    .WithSenderAddress(Sender).WithHash(hash).TestObject, null)
                { GasBottleneck = gasBottleneck }
                : AccountTx(nonce, gasBottleneck, hash);
        }

        TxDistinctSortedPool pool = Pool(blobs: false, candidates);
        for (int step = 0; step < 300; step++)
        {
            MempoolTransaction tx = candidates[random.Next(candidates.Length)];
            switch (random.Next(3))
            {
                case 0:
                    pool.TryInsert(tx.Hash!, tx);
                    break;
                case 1:
                    pool.TryRemove(tx.Hash!);
                    break;
                default:
                    if (pool.ContainsKey(tx.Hash!)) Reprice(pool, tx, (uint)random.Next(1, 200));
                    break;
            }

            Assert.That(pool.GetBucketSnapshot(Sender), Is.EqualTo(TreeOrder(pool)).Using<MempoolTransaction>(ReferenceEquals), $"step {step}");
        }
    }

    private static MempoolTransaction[] TreeOrder(TxDistinctSortedPool pool)
    {
        MempoolTransaction[] order = [];
        pool.UpdatePool(Substitute.For<IAccountStateProvider>(),
            (in AccountStruct _, EnhancedSortedSet<MempoolTransaction> bucket, ref MempoolTransaction? _, TxDistinctSortedPool.UpdateTransactionDelegate _) =>
            {
                List<MempoolTransaction> walked = [];
                foreach (MempoolTransaction tx in bucket) walked.Add(tx);
                order = [.. walked];
            });
        return order;
    }

    private static MempoolTransaction OtherSenderTx(string seed, uint gasBottleneck)
    {
        Hash256 hash = Keccak.Compute(seed);
        return new MempoolTransaction(Build.A.Transaction
            .WithNonce(0)
            .WithSenderAddress(new Address(hash.Bytes[..20]))
            .WithHash(hash)
            .TestObject, null)
        { GasBottleneck = gasBottleneck };
    }

    /// <summary>A pool whose worst-value heap a test can rewrite, to build heap states directly.</summary>
    /// <remarks>Every transaction here carries a gas bottleneck, so the comparer never reads the head or the spec.</remarks>
    private sealed class HeapEditablePool(int capacity) : TxDistinctSortedPool(capacity,
        new GasPriceTxComparer(Substitute.For<IBlockFinder>(), Substitute.For<ISpecProvider>()), LimboLogs.Instance)
    {
        /// <summary>Leaves only <paramref name="entry"/> in the heap, or nothing, and refreshes the worst value.</summary>
        public void ReplaceWorstValues(MempoolTransaction? entry)
        {
            using McsLock.Disposable lockRelease = Lock.Acquire();

            while (_worstSortedValues.Count > 0) _worstSortedValues.Remove(_worstSortedValues.Max.Key);
            if (entry is not null) _worstSortedValues.Add(entry, entry.Hash!);
            UpdateWorstValue();
        }
    }
}
