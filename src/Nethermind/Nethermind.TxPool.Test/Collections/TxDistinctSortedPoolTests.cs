// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Core;
using Nethermind.Core.Collections;
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

    /// <summary>
    /// A keyed transaction's <c>Nonce</c> is a sequence in its own domain, so it can tie an account-nonce
    /// transaction of the same sender and leave the bucket order to the gas-bottleneck comparer. Repricing the
    /// entry then strands it at its old tree position, and the bucket removal the pool attempts next fails —
    /// the account-domain count must follow the bucket, not the attempt.
    /// </summary>
    [Test]
    public void Keeps_the_account_domain_count_when_a_reprice_blocks_the_bucket_removal()
    {
        Transaction accountTx = Build.A.Transaction
            .WithNonce(SharedNonce)
            .WithSenderAddress(Sender)
            .WithGasBottleneck(100)
            .WithHash(TestItem.KeccakA)
            .TestObject;
        Transaction keyedTx = Build.A.Transaction
            .WithType(TxType.FrameTx)
            .WithNonce(SharedNonce)
            .WithNonceKeys(0xbeef)
            .WithSenderAddress(Sender)
            .WithGasBottleneck(50)
            .WithHash(TestItem.KeccakB)
            .TestObject;

        TxDistinctSortedPool pool = Pool(blobs: false, accountTx, keyedTx);
        Assert.That(pool.GetAccountDomainBucketCount(Sender), Is.EqualTo(1), "only the account-nonce entry fills the sender's nonce window");

        // The sequence UpdateGasBottleneckAndMarkForEviction runs: the entry is queued for removal, then
        // repriced in the same pass, so the removal at the end of the pass meets a moved ordering key.
        pool.UpdatePool(Substitute.For<IAccountStateProvider>(),
            (in AccountStruct _, EnhancedSortedSet<Transaction> bucket, ref Transaction? lastElement, TxDistinctSortedPool.UpdateTransactionDelegate updateTx) =>
            {
                updateTx(bucket, keyedTx, changedGasBottleneck: null, lastElement);
                updateTx(bucket, keyedTx, (UInt256)200, lastElement);
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.GetBucketCount(Sender), Is.EqualTo(2), "the failed bucket removal leaves the keyed entry behind");
            Assert.That(pool.GetAccountDomainBucketCount(Sender), Is.EqualTo(1),
                "an entry the bucket still holds must stay counted, or GapNonceFilter admits a nonce nothing can fill");
        }
    }
}
