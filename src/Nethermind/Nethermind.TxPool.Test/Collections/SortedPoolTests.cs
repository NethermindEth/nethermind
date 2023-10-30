// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    public class SortedPoolTests
    {
        private const int Capacity = 16;

        private SortedPool<ValueHash256, Transaction, Address> _sortedPool;

        private Transaction[] _transactions = new Transaction[Capacity * 8];

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
                var tx = _transactions[^(i + 1)];
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
                var tx = _transactions[i];
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
    }
}
