// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus.Comparers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.TxPool.Collections;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test.Collections
{
    [TestFixture]
    [Parallelizable(ParallelScope.None)]
    public class DistinctValueSortedPoolTests
    {
        private const int Capacity = 16;
        private ITransactionComparerProvider _transactionComparerProvider;

        private static Transaction[] GenerateTransactions(int count = Capacity, UInt256? gasPrice = null, Address address = null, UInt256? nonce = null) =>
            Enumerable.Range(0, count).Select(i =>
            {
                UInt256 iUint256 = (UInt256)i;
                var transaction = Build.A.Transaction.WithGasPrice(gasPrice ?? iUint256).WithNonce(nonce ?? iUint256)
                    .WithSenderAddress(address ?? TestItem.Addresses[i]).TestObject;
                transaction.Hash = Keccak.Compute(i.ToString());
                return transaction;
            }).ToArray();

        [SetUp]
        public void Setup()
        {
            CollectAndFinalize();
            _finalizedCount = 0;
            _allCount = 0;

            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            Block block = Build.A.Block.WithNumber(0).TestObject;
            blockTree.Head.Returns(block);
            specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(new ReleaseSpec() { IsEip1559Enabled = false });
            _transactionComparerProvider = new TransactionComparerProvider(specProvider, blockTree);
        }

        public static IEnumerable DistinctTestCases
        {
            get
            {
                yield return new TestCaseData(new object[] { GenerateTransactions(), Capacity });
                yield return new TestCaseData(new object[] { GenerateTransactions(gasPrice: 5, nonce: 5), Capacity });
                yield return new TestCaseData(new object[] { GenerateTransactions(gasPrice: 5, address: TestItem.AddressA), Capacity });
                yield return new TestCaseData(new object[] { GenerateTransactions(gasPrice: null, nonce: 5, address: TestItem.AddressA), 1 });
                yield return new TestCaseData(new object[] { GenerateTransactions(Capacity * 10), Capacity });
                yield return new TestCaseData(new object[] { GenerateTransactions(Capacity * 10, gasPrice: 5, nonce: 5), Capacity });
                yield return new TestCaseData(new object[] { GenerateTransactions(Capacity * 10, gasPrice: 5, address: TestItem.AddressA), Capacity });
                yield return new TestCaseData(new object[] { GenerateTransactions(Capacity * 10, gasPrice: null, nonce: 5, address: TestItem.AddressA), 1 });
            }
        }

        [TestCaseSource(nameof(DistinctTestCases))]
        public void Distinct_transactions_are_all_added(Transaction[] transactions, int expectedCount)
        {

            var pool = new TxDistinctSortedPool(Capacity, _transactionComparerProvider.GetDefaultComparer(), LimboLogs.Instance);

            foreach (var transaction in transactions)
            {
                pool.TryInsert(transaction.Hash.ValueKeccak, transaction);
            }

            pool.Count.Should().Be(expectedCount);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Same_transactions_are_all_replaced_with_highest_gas_price(bool gasPriceAscending)
        {
            var pool = new TxDistinctSortedPool(Capacity, _transactionComparerProvider.GetDefaultComparer(), LimboLogs.Instance);

            var transactions = gasPriceAscending
                ? GenerateTransactions(address: TestItem.AddressB, nonce: 3).OrderBy(t => t.GasPrice)
                : GenerateTransactions(address: TestItem.AddressB, nonce: 3).OrderByDescending(t => t.GasPrice);

            foreach (var transaction in transactions)
            {
                pool.TryInsert(transaction.Hash.ValueKeccak, transaction);
            }

            pool.Count.Should().Be(1);
            pool.GetSnapshot().First().GasPrice.Should().Be(Capacity - 1);
        }

        private static int _finalizedCount;
        private static int _allCount;

        private class WithFinalizer
        {
            public int Index { get; }

            public WithFinalizer()
            {
                Index = Interlocked.Increment(ref _allCount);
            }

            public WithFinalizer(int index)
            {
                Index = index;
                Interlocked.Increment(ref _allCount);
            }

            ~WithFinalizer()
            {
                Interlocked.Increment(ref _finalizedCount);
            }
        }

        private class WithFinalizerComparer : IEqualityComparer<WithFinalizer>
        {
            public bool Equals(WithFinalizer x, WithFinalizer y)
            {
                return x?.Index == y?.Index;
            }

            public int GetHashCode(WithFinalizer obj)
            {
                return obj.Index.GetHashCode();
            }
        }

        private class WithFinalizerDistinctPool : DistinctValueSortedPool<int, WithFinalizer, int>
        {
            public WithFinalizerDistinctPool(int capacity, IComparer<WithFinalizer> comparer, IEqualityComparer<WithFinalizer> distinctComparer, ILogManager logManager)
                : base(capacity, comparer, distinctComparer, logManager)
            {
            }

            protected override IComparer<WithFinalizer> GetUniqueComparer(IComparer<WithFinalizer> comparer) => comparer;

            protected override IComparer<WithFinalizer> GetGroupComparer(IComparer<WithFinalizer> comparer) => comparer;

            protected override int MapToGroup(WithFinalizer value) => value.Index;
            protected override int GetKey(WithFinalizer value) => value.Index;
        }

        [Test]
        public void Capacity_is_never_exceeded()
        {
            IComparer<WithFinalizer> comparer = Comparer<WithFinalizer>.Create((t1, t2) =>
            {
                int t1Oddity = t1.Index % 2;
                int t2Oddity = t2.Index % 2;

                if (t1Oddity.CompareTo(t2Oddity) != 0)
                {
                    return t1Oddity.CompareTo(t2Oddity);
                }

                return t1.Index.CompareTo(t2.Index);
            });

            var pool = new WithFinalizerDistinctPool(Capacity, comparer, new WithFinalizerComparer(), LimboLogs.Instance);

            int capacityMultiplier = 10;
            int expectedAllCount = Capacity * capacityMultiplier;

            WithFinalizer newOne;
            for (int i = 0; i < expectedAllCount; i++)
            {
                newOne = new WithFinalizer();
                pool.TryInsert(newOne.Index, newOne);
            }

            newOne = null;

            CollectAndFinalize();

            _allCount.Should().Be(expectedAllCount);
            _finalizedCount.Should().BeLessOrEqualTo(expectedAllCount - Capacity);
            pool.Count.Should().Be(Capacity);
        }

        [Test]
        public void Capacity_is_never_exceeded_when_there_are_duplicates()
        {
            Comparer<WithFinalizer> comparer = Comparer<WithFinalizer>.Create((t1, t2) =>
            {
                int t1Oddity = t1.Index % 2;
                int t2Oddity = t2.Index % 2;

                if (t1Oddity.CompareTo(t2Oddity) != 0)
                {
                    return t1Oddity.CompareTo(t2Oddity);
                }

                return t1.Index.CompareTo(t2.Index);
            });

            var pool = new WithFinalizerDistinctPool(Capacity, comparer, new WithFinalizerComparer(), LimboLogs.Instance);

            int capacityMultiplier = 10;

            for (int i = 0; i < Capacity * capacityMultiplier; i++)
            {
                WithFinalizer newOne = new(i % (Capacity * 2));
                pool.TryInsert(newOne.Index, newOne);
            }

            CollectAndFinalize();

            _finalizedCount.Should().BeLessOrEqualTo(Capacity * (capacityMultiplier - 1));
            _allCount.Should().Be(Capacity * capacityMultiplier);
            pool.Count.Should().Be(Capacity);
        }

        [Test]
        public async Task Capacity_is_never_exceeded_with_multiple_threads()
        {
            int capacityMultiplier = 10;
            _finalizedCount.Should().Be(0);
            _allCount.Should().Be(0);

            IComparer<WithFinalizer> comparer = Comparer<WithFinalizer>.Create((t1, t2) =>
            {
                int t1Oddity = t1.Index % 2;
                int t2Oddity = t2.Index % 2;

                if (t1Oddity.CompareTo(t2Oddity) != 0)
                {
                    return t1Oddity.CompareTo(t2Oddity);
                }

                return t1.Index.CompareTo(t2.Index);
            });

            var pool = new WithFinalizerDistinctPool(Capacity, comparer, new WithFinalizerComparer(), LimboLogs.Instance);

            void KeepGoing(int iterations)
            {
                for (int i = 0; i < iterations; i++)
                {
                    if (i % 3 == 2)
                    {
                        pool.TryRemove(i - 1);
                    }

                    pool.TryInsert(i, new WithFinalizer(i));

                    if (i % 3 == 1)
                    {
                        pool.GetSnapshot();
                    }
                }
            }

            Task a = new(() => KeepGoing(Capacity * capacityMultiplier));
            Task b = new(() => KeepGoing(Capacity * capacityMultiplier));
            Task c = new(() => KeepGoing(Capacity * capacityMultiplier));

            a.Start();
            b.Start();
            c.Start();

            await Task.WhenAll(a, b, c);

            CollectAndFinalize();

            int expectedAllCount = Capacity * capacityMultiplier * 3;
            _allCount.Should().Be(expectedAllCount);
            _finalizedCount.Should().BeGreaterOrEqualTo(expectedAllCount - Capacity);
        }

        private static void CollectAndFinalize()
        {
            GC.Collect(2, GCCollectionMode.Forced);
            GC.WaitForFullGCComplete();
            GC.Collect(2, GCCollectionMode.Forced);
            GC.WaitForPendingFinalizers();
        }
    }
}
