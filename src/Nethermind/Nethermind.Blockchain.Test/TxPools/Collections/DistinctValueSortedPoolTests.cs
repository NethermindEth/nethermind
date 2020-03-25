﻿//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.TxPool;
using Nethermind.TxPool.Collections;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.TxPools.Collections
{
    [TestFixture]
    [Parallelizable(ParallelScope.None)]
    public class DistinctValueSortedPoolTests
    {
        private const int Capacity = 16;

        private static Transaction[] GenerateTransactions(int count = Capacity, UInt256? gasPrice = null, Address address = null, UInt256? nonce = null) =>
            Enumerable.Range(0, count).Select(i =>
            {
                UInt256.Create(out UInt256 iUint256, i);
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
        }

        public static IEnumerable DistinctTestCases
        {
            get
            {
                yield return new TestCaseData(new object[] {GenerateTransactions(), Capacity});
                yield return new TestCaseData(new object[] {GenerateTransactions(gasPrice: 5, nonce: 5), Capacity});
                yield return new TestCaseData(new object[] {GenerateTransactions(gasPrice: 5, address: TestItem.AddressA), Capacity});
                yield return new TestCaseData(new object[] {GenerateTransactions(gasPrice: null, nonce: 5, address: TestItem.AddressA), 1});
                yield return new TestCaseData(new object[] {GenerateTransactions(Capacity * 10), Capacity});
                yield return new TestCaseData(new object[] {GenerateTransactions(Capacity * 10, gasPrice: 5, nonce: 5), Capacity});
                yield return new TestCaseData(new object[] {GenerateTransactions(Capacity * 10, gasPrice: 5, address: TestItem.AddressA), Capacity});
                yield return new TestCaseData(new object[] {GenerateTransactions(Capacity * 10, gasPrice: null, nonce: 5, address: TestItem.AddressA), 1});
            }
        }

        [TestCaseSource(nameof(DistinctTestCases))]
        public void Distinct_transactions_are_all_added(Transaction[] transactions, int expectedCount)
        {
            var pool = new DistinctValueSortedPool<Keccak, Transaction>(Capacity, (t1, t2) => t1.GasPrice.CompareTo(t2.GasPrice), PendingTransactionComparer.Default);

            foreach (var transaction in transactions)
            {
                pool.TryInsert(transaction.Hash, transaction);
            }

            pool.Count.Should().Be(expectedCount);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Same_transactions_are_all_replaced_with_highest_gas_price(bool gasPriceAscending)
        {
            var pool = new DistinctValueSortedPool<Keccak, Transaction>(Capacity, (t1, t2) => t1.GasPrice.CompareTo(t2.GasPrice), PendingTransactionComparer.Default);

            var transactions = gasPriceAscending
                ? GenerateTransactions(address: TestItem.AddressB, nonce: 3).OrderBy(t => t.GasPrice)
                : GenerateTransactions(address: TestItem.AddressB, nonce: 3).OrderByDescending(t => t.GasPrice);

            foreach (var transaction in transactions)
            {
                pool.TryInsert(transaction.Hash, transaction);
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

        [Test]
        public void Capacity_is_never_exceeded()
        {
            var pool = new DistinctValueSortedPool<int, WithFinalizer>(Capacity, (t1, t2) =>
            {
                int t1Oddity = t1.Index % 2;
                int t2Oddity = t2.Index % 2;

                if (t1Oddity.CompareTo(t2Oddity) != 0)
                {
                    return t1Oddity.CompareTo(t2Oddity);
                }

                return t1.Index.CompareTo(t2.Index);
            }, new WithFinalizerComparer());

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
            var pool = new DistinctValueSortedPool<int, WithFinalizer>(Capacity, (t1, t2) =>
            {
                int t1Oddity = t1.Index % 2;
                int t2Oddity = t2.Index % 2;

                if (t1Oddity.CompareTo(t2Oddity) != 0)
                {
                    return t1Oddity.CompareTo(t2Oddity);
                }

                return t1.Index.CompareTo(t2.Index);
            }, new WithFinalizerComparer());

            int capacityMultiplier = 10;

            for (int i = 0; i < Capacity * capacityMultiplier; i++)
            {
                WithFinalizer newOne = new WithFinalizer(i % (Capacity * 2));
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

            var pool = new DistinctValueSortedPool<int, WithFinalizer>(Capacity, (t1, t2) =>
            {
                int t1Oddity = t1.Index % 2;
                int t2Oddity = t2.Index % 2;

                if (t1Oddity.CompareTo(t2Oddity) != 0)
                {
                    return t1Oddity.CompareTo(t2Oddity);
                }

                return t1.Index.CompareTo(t2.Index);
            }, new WithFinalizerComparer());

            void KeepGoing(int iterations)
            {
                for (int i = 0; i < iterations; i++)
                {
                    if (i % 3 == 2)
                    {
                        pool.TryRemove(i - 1, out _);
                    }

                    pool.TryInsert(i, new WithFinalizer(i));

                    if (i % 3 == 1)
                    {
                        pool.GetSnapshot();
                    }
                }
            }

            Task a = new Task(() => KeepGoing(Capacity * capacityMultiplier));
            Task b = new Task(() => KeepGoing(Capacity * capacityMultiplier));
            Task c = new Task(() => KeepGoing(Capacity * capacityMultiplier));

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