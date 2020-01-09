//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.Forks;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
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

        public static IEnumerable DistinctTestCases
        {
            get
            {
                yield return new TestCaseData(new object[] {GenerateTransactions()});
                yield return new TestCaseData(new object[] {GenerateTransactions(gasPrice:5, nonce:5)});
                yield return new TestCaseData(new object[] {GenerateTransactions(gasPrice:5, address:TestItem.AddressA)});
            }
        }

        [TestCaseSource(nameof(DistinctTestCases))]
        public void Distinct_transactions_are_all_added(Transaction[] transactions)
        {
            var pool = new DistinctValueSortedPool<Keccak, Transaction>(Capacity, (t1, t2) => t1.GasPrice.CompareTo(t2.GasPrice), PendingTransactionComparer.Default);
            
            foreach (var transaction in transactions)
            {
                pool.TryInsert(transaction.Hash, transaction);
            }

            pool.Count.Should().Be(transactions.Length);
        }
       
        [TestCase(true)]
        [TestCase(false)]
        public void Same_transactions_are_all_replaced_with_highest_gas_price(bool gasPriceAscending)
        {
            var pool = new DistinctValueSortedPool<Keccak, Transaction>(Capacity, (t1, t2) => t1.GasPrice.CompareTo(t2.GasPrice), PendingTransactionComparer.Default);
            
            var transactions = gasPriceAscending 
                ? GenerateTransactions(address:TestItem.AddressB, nonce:3).OrderBy(t => t.GasPrice)
                : GenerateTransactions(address:TestItem.AddressB, nonce:3).OrderByDescending(t => t.GasPrice);
            
            foreach (var transaction in transactions)
            {
                pool.TryInsert(transaction.Hash, transaction);
            }

            pool.Count.Should().Be(1);
            pool.GetSnapshot().First().GasPrice.Should().Be(Capacity - 1);
        }
    }
}