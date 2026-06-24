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
            yield return new TestCaseData(Array.Empty<int>(), int.MaxValue, Array.Empty<int>())
                .SetName("VisitBucket_missing_group_visits_nothing");
            yield return new TestCaseData(new[] { 0, 1, 2, 3 }, int.MaxValue, new[] { 0, 1, 2, 3 })
                .SetName("VisitBucket_iterates_all_items_in_ascending_nonce_order");
            yield return new TestCaseData(new[] { 0, 1, 2, 3 }, 2, new[] { 0, 1, 2 })
                .SetName("VisitBucket_stops_after_visitor_returns_false");
        }

        [TestCaseSource(nameof(VisitBucketCases))]
        public void VisitBucket_visits_expected_nonces(int[] insertNonces, int stopAfterNonce, int[] expectedVisited)
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

        private void InsertNonces(Address sender, ReadOnlySpan<int> nonces)
        {
            foreach (int nonce in nonces)
            {
                Transaction tx = Build.A.Transaction
                    .WithNonce((UInt256)nonce)
                    .WithSenderAddress(sender)
                    .TestObject;
                tx.Hash = tx.CalculateHash();
                _sortedPool.TryInsert(tx.Hash, tx);
            }
        }
    }
}
