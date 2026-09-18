// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus.Comparers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.TxPool.Collections;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test.Collections
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    [FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
    public class SortedPoolTests
    {
        private const int Capacity = 16;

        private SortedPool<ValueHash256, Transaction, AddressAsKey> _sortedPool;

        private readonly Transaction[] _transactions = new Transaction[Capacity * 8];

        [SetUp]
        public void Setup()
        {
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            Block block = Build.A.Block.WithNumber(0).TestObject;
            blockTree.Head.Returns(block);
            specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(new ReleaseSpec() { IsEip1559Enabled = false });
            ITransactionComparerProvider transactionComparerProvider = new TransactionComparerProvider(specProvider, blockTree);
            _sortedPool = new TxDistinctSortedPool(Capacity, transactionComparerProvider.GetDefaultComparer(), LimboLogs.Instance);
            for (int i = 0; i < _transactions.Length; i++)
            {
                UInt256 gasPrice = (UInt256)i;
                _transactions[i] = Build.A.Transaction.WithGasPrice(gasPrice)
                    .WithSenderAddress(Address.FromNumber(gasPrice)).TestObject;
            }
        }

        [Test]
        public void Production_snapshot_reuses_only_unchanged_buckets()
        {
            Transaction first = _transactions[1];
            Transaction other = _transactions[2];
            InsertSnapshotTransaction(first);
            InsertSnapshotTransaction(other);
            Dictionary<AddressAsKey, Transaction[]> before = _sortedPool.GetProductionSnapshot();
            Dictionary<AddressAsKey, Transaction[]> unchanged = _sortedPool.GetProductionSnapshot();
            Assert.That(unchanged, Is.SameAs(before));

            Transaction next = Build.A.Transaction.WithSenderAddress(first.SenderAddress!).WithNonce(1).WithGasPrice(10).TestObject;
            InsertSnapshotTransaction(next);
            Dictionary<AddressAsKey, Transaction[]> added = _sortedPool.GetProductionSnapshot();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(added[first.SenderAddress!], Is.EqualTo(new[] { first, next }));
                Assert.That(before[first.SenderAddress!], Is.EqualTo(new[] { first }));
                Assert.That(added[other.SenderAddress!], Is.SameAs(before[other.SenderAddress!]));
            }

            _sortedPool.TryRemove(first.Hash!);
            Assert.That(_sortedPool.GetProductionSnapshot()[first.SenderAddress!], Is.EqualTo(new[] { next }));
            _sortedPool.TryRemove(next.Hash!);
            Assert.That(_sortedPool.GetProductionSnapshot().ContainsKey(first.SenderAddress!), Is.False);
            InsertSnapshotTransaction(first);
            Assert.That(_sortedPool.GetProductionSnapshot()[first.SenderAddress!], Is.EqualTo(new[] { first }));
        }

        [Test]
        public void Production_snapshot_observes_replacements_and_rechecks_filter()
        {
            Transaction first = _transactions[1];
            InsertSnapshotTransaction(first);
            Dictionary<AddressAsKey, Transaction[]> before = _sortedPool.GetProductionSnapshot();
            Transaction replacement = Build.A.Transaction.WithSenderAddress(first.SenderAddress!).WithGasPrice(100).TestObject;
            InsertSnapshotTransaction(replacement);
            Assert.That(_sortedPool.GetProductionSnapshot()[first.SenderAddress!], Is.EqualTo(new[] { replacement }));
            Assert.That(_sortedPool.GetProductionSnapshot(_ => false), Is.Empty);
            Assert.That(_sortedPool.GetProductionSnapshot(_ => true)[first.SenderAddress!], Is.EqualTo(new[] { replacement }));
            Assert.That(before[first.SenderAddress!], Is.EqualTo(new[] { first }));
        }

        [Test]
        public void Public_bucket_snapshots_do_not_expose_cached_arrays()
        {
            Transaction first = _transactions[1];
            InsertSnapshotTransaction(first);
            Transaction[] cached = _sortedPool.GetProductionSnapshot()[first.SenderAddress!];
            _sortedPool.GetBucketSnapshot()[first.SenderAddress!][0] = _transactions[2];
            _sortedPool.GetBucketSnapshot(first.SenderAddress!)[0] = _transactions[2];
            Assert.That(_sortedPool.GetProductionSnapshot()[first.SenderAddress!], Is.SameAs(cached).And.EqualTo(new[] { first }));
        }

        [Test]
        public void Production_snapshot_discards_evicted_buckets()
        {
            for (int i = 1; i <= Capacity; i++) InsertSnapshotTransaction(_transactions[i]);
            Dictionary<AddressAsKey, Transaction[]> before = _sortedPool.GetProductionSnapshot();
            InsertSnapshotTransaction(_transactions[Capacity + 1]);
            Dictionary<AddressAsKey, Transaction[]> after = _sortedPool.GetProductionSnapshot();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(after.Count, Is.EqualTo(Capacity));
                Assert.That(after.ContainsKey(_transactions[1].SenderAddress!), Is.False);
                Assert.That(after[_transactions[Capacity + 1].SenderAddress!], Is.EqualTo(new[] { _transactions[Capacity + 1] }));
                Assert.That(before.ContainsKey(_transactions[1].SenderAddress!), Is.True);
            }
        }

        private void InsertSnapshotTransaction(Transaction transaction)
        {
            transaction.Hash = transaction.CalculateHash();
            Assert.That(_sortedPool.TryInsert(transaction.Hash!, transaction), Is.True);
        }

        [Test]
        public void Beyond_capacity()
        {
            for (int i = 0; i < _transactions.Length; i++)
            {
                Transaction tx = _transactions[^(i + 1)];
                tx.Hash = tx.CalculateHash();
                _sortedPool.TryInsert(tx.Hash, tx);
                Assert.That(_sortedPool.TryGetValue(tx.Hash, out Transaction txOther) ? txOther : null, Is.EqualTo(i > 15 ? null : tx));
                Assert.That(_sortedPool.Count, Is.EqualTo(Math.Min(16, i + 1)));
            }

            Assert.That(_sortedPool.GetSnapshot().Length, Is.EqualTo(Capacity));

            for (int i = 0; i < Capacity; i++)
            {
                _sortedPool.TryTakeFirst(out Transaction tx);
                UInt256 gasPrice = (UInt256)(_transactions.Length - i - 1);
                Assert.That(_sortedPool.Count, Is.EqualTo(Capacity - i - 1));
                Assert.That(tx.GasPrice, Is.EqualTo(gasPrice));
            }
        }

        [Test]
        public void Beyond_capacity_ordered()
        {
            for (int i = 0; i < _transactions.Length; i++)
            {
                Transaction tx = _transactions[i];
                tx.Hash = tx.CalculateHash();
                _sortedPool.TryInsert(tx.Hash, tx);
                Assert.That(_sortedPool.Count, Is.EqualTo(Math.Min(16, i + 1)));
            }

            for (int i = 0; i < Capacity; i++)
            {
                _sortedPool.TryTakeFirst(out Transaction tx);
                UInt256 gasPrice = (UInt256)(_transactions.Length - i - 1);
                Assert.That(_sortedPool.Count, Is.EqualTo(Capacity - i - 1));
                Assert.That(tx.GasPrice, Is.EqualTo(gasPrice));
            }
        }

        [Test]
        public void should_remove_empty_buckets()
        {
            Transaction tx = Build.A.Transaction
                .WithSenderAddress(TestItem.AddressA)
                .WithHash(TestItem.KeccakA).TestObject;

            _sortedPool.TryInsert(tx.Hash, tx);
            Assert.That(_sortedPool.TryGetBucket(tx.SenderAddress, out _), Is.True);

            _sortedPool.TryRemove(tx.Hash);
            Assert.That(_sortedPool.TryGetBucket(tx.SenderAddress, out _), Is.False);
        }

        [Test]
        public void GetBest_returns_first_transaction_without_removing_it()
        {
            Transaction lowPriority = Build.A.Transaction
                .WithGasPrice(1)
                .WithSenderAddress(TestItem.AddressA)
                .TestObject;
            lowPriority.Hash = lowPriority.CalculateHash();
            Transaction highPriority = Build.A.Transaction
                .WithGasPrice(2)
                .WithSenderAddress(TestItem.AddressB)
                .TestObject;
            highPriority.Hash = highPriority.CalculateHash();

            _sortedPool.TryInsert(lowPriority.Hash, lowPriority);
            _sortedPool.TryInsert(highPriority.Hash, highPriority);

            Assert.That(_sortedPool.GetBest(), Is.EqualTo(highPriority));
            Assert.That(_sortedPool.Count, Is.EqualTo(2));
        }

        private static IEnumerable<TestCaseData> VisitBucketCases()
        {
            yield return new TestCaseData(Array.Empty<ulong>(), int.MaxValue, Array.Empty<int>())
                .SetName("VisitBucket_missing_group_visits_nothing");
            yield return new TestCaseData(new ulong[] { 0, 1, 2, 3 }, int.MaxValue, new[] { 0, 1, 2, 3 })
                .SetName("VisitBucket_iterates_all_items_in_ascending_nonce_order");
            yield return new TestCaseData(new ulong[] { 0, 1, 2, 3 }, 2, new[] { 0, 1, 2 })
                .SetName("VisitBucket_stops_after_visitor_returns_false");
        }

        [TestCaseSource(nameof(VisitBucketCases))]
        public void VisitBucket_visits_expected_nonces(ulong[] insertNonces, int stopAfterNonce, int[] expectedVisited)
        {
            InsertNonces(TestItem.AddressA, insertNonces);

            (List<int> Visited, int StopAfter) state = (new List<int>(), stopAfterNonce);
            _sortedPool.VisitBucket(TestItem.AddressA, ref state, static (Transaction tx, ref (List<int> Visited, int StopAfter) s) =>
            {
                s.Visited.Add((int)tx.Nonce);
                return (int)tx.Nonce < s.StopAfter;
            });

            Assert.That(state.Visited, Is.EqualTo(expectedVisited));
        }

        [Test]
        public void VisitBucket_throws_on_null_visitor()
        {
            int unused = 0;
            Action act = () => _sortedPool.VisitBucket(TestItem.AddressA, ref unused, null!);

            Assert.That(act, Throws.TypeOf<ArgumentNullException>());
        }

        private void InsertNonces(Address sender, ReadOnlySpan<ulong> nonces)
        {
            foreach (ulong nonce in nonces)
            {
                Transaction tx = Build.A.Transaction
                    .WithNonce(nonce)
                    .WithSenderAddress(sender)
                    .TestObject;
                tx.Hash = tx.CalculateHash();
                _sortedPool.TryInsert(tx.Hash, tx);
            }
        }
    }
}
