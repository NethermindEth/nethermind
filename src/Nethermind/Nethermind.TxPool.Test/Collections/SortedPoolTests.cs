// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
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
            _sortedPool.TryGetBucket(tx.SenderAddress, out _).Should().BeTrue();

            _sortedPool.TryRemove(tx.Hash);
            _sortedPool.TryGetBucket(tx.SenderAddress, out _).Should().BeFalse();
        }

        [Test]
        public void VisitBucket_missing_group_does_not_invoke_visitor()
        {
            int visited = 0;
            _sortedPool.VisitBucket(TestItem.AddressA, ref visited, static (Transaction _, ref int count) =>
            {
                count++;
                return true;
            });

            visited.Should().Be(0);
        }

        [Test]
        public void VisitBucket_iterates_all_items_in_ascending_nonce_order()
        {
            InsertNonces(TestItem.AddressA, [0, 1, 2, 3]);

            List<UInt256> visited = new();
            _sortedPool.VisitBucket(TestItem.AddressA, ref visited, static (Transaction tx, ref List<UInt256> acc) =>
            {
                acc.Add(tx.Nonce);
                return true;
            });

            visited.Should().Equal((UInt256)0, (UInt256)1, (UInt256)2, (UInt256)3);
        }

        [Test]
        public void VisitBucket_stops_when_visitor_returns_false()
        {
            InsertNonces(TestItem.AddressA, [0, 1, 2, 3]);

            (int Count, UInt256 StopAt) state = (0, StopAt: (UInt256)2);
            _sortedPool.VisitBucket(TestItem.AddressA, ref state, static (Transaction tx, ref (int Count, UInt256 StopAt) s) =>
            {
                s.Count++;
                return tx.Nonce < s.StopAt;
            });

            state.Count.Should().Be(3);
        }

        [Test]
        public void VisitBucket_propagates_state_mutations_via_ref()
        {
            InsertNonces(TestItem.AddressA, [0, 1, 2]);

            UInt256 sum = UInt256.Zero;
            _sortedPool.VisitBucket(TestItem.AddressA, ref sum, static (Transaction tx, ref UInt256 acc) =>
            {
                acc += tx.Nonce;
                return true;
            });

            sum.Should().Be((UInt256)3);
        }

        [Test]
        public void VisitBucket_throws_on_null_visitor()
        {
            int unused = 0;
            Action act = () => _sortedPool.VisitBucket(TestItem.AddressA, ref unused, null!);

            act.Should().Throw<ArgumentNullException>();
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
