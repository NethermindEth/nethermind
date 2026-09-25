// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
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

    private static Transaction AccountTx(ulong nonce, uint gasBottleneck, Hash256 hash) => Build.A.Transaction
        .WithNonce(nonce)
        .WithSenderAddress(Sender)
        .WithGasBottleneck(gasBottleneck)
        .WithHash(hash)
        .TestObject;

    private static Transaction KeyedTx() => Build.A.Transaction
        .WithType(TxType.FrameTx)
        .WithNonce(SharedNonce)
        .WithNonceKeys(0xbeef)
        .WithSenderAddress(Sender)
        .WithGasBottleneck(50)
        .WithHash(TestItem.KeccakB)
        .TestObject;

    /// <summary>Marks <paramref name="keyedTx"/> for removal and reprices <paramref name="sibling"/> in the same
    /// pass, which is what <c>UpdateGasBottleneckAndMarkForEviction</c> does when both share a nonce.</summary>
    private static void MarkKeyedAndReprice(TxDistinctSortedPool pool, Transaction keyedTx, Transaction sibling, uint siblingGasBottleneck) =>
        pool.UpdatePool(Substitute.For<IAccountStateProvider>(),
            (in AccountStruct _, EnhancedSortedSet<Transaction> bucket, ref Transaction? lastElement, TxDistinctSortedPool.UpdateTransactionDelegate updateTx) =>
            {
                updateTx(bucket, keyedTx, changedGasBottleneck: null, lastElement);
                updateTx(bucket, sibling, (UInt256)siblingGasBottleneck, lastElement);
            });

    private static void Reprice(TxDistinctSortedPool pool, Transaction tx, uint gasBottleneck) =>
        pool.UpdatePool(Substitute.For<IAccountStateProvider>(),
            (in AccountStruct _, EnhancedSortedSet<Transaction> bucket, ref Transaction? lastElement, TxDistinctSortedPool.UpdateTransactionDelegate updateTx) =>
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
        Transaction n4 = AccountTx(4, 110, TestItem.KeccakA);
        Transaction accountTx = AccountTx(SharedNonce, 100, TestItem.KeccakC);
        Transaction keyedTx = KeyedTx();
        Transaction n6 = AccountTx(6, 90, TestItem.KeccakD);
        Transaction n7 = AccountTx(7, 80, TestItem.KeccakE);

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
        Transaction n4 = AccountTx(4, 110, TestItem.KeccakA);
        Transaction accountTx = AccountTx(SharedNonce, 100, TestItem.KeccakC);
        Transaction keyedTx = KeyedTx();
        Transaction n6 = AccountTx(6, 90, TestItem.KeccakD);
        Transaction n7 = AccountTx(7, 80, TestItem.KeccakE);

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
            Transaction filler = Build.A.Transaction
                .WithNonce(0)
                .WithSenderAddress(new Address(hash.Bytes[..20]))
                .WithGasBottleneck(1000)
                .WithHash(hash)
                .TestObject;
            pool.TryInsert(filler.Hash!, filler);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.GetBucketCount(Sender), Is.EqualTo(1), "eviction swept the stranded entry out of the bucket");
            Assert.That(pool.GetAccountDomainBucketCount(Sender), Is.EqualTo(1),
                "the keyed count must not survive the entry, or the sender's nonce window shrinks by one for good");
        }
    }
}
